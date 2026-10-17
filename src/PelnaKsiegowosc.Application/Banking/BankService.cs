using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Application.Banking;

public sealed record ImportResult(Guid StatementId, int Imported, int Duplicates, IReadOnlyList<string> Warnings);

public sealed record MatchSuggestion(Guid TransactionId, Guid OpenItemId, string Reference, Guid CounterpartyId, string CounterpartyName, decimal Remaining, int Score, string Reason);

public sealed record Allocation(Guid OpenItemId, decimal Amount);

/// <summary>Bank i kasa: import wyciągów (CSV wg profilu, MT940), kontrola duplikatów, dopasowanie do rozrachunków, księgowanie płatności.</summary>
public sealed class BankService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public BankService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = new AuditService(db, clock);
    }

    public async Task<MoneyAccount> CreateMoneyAccountAsync(MoneyAccountKind kind, string name, string ledgerAccountNumber, string? iban, string currency = "PLN", CancellationToken ct = default)
    {
        _user.Require(Permission.ManageBank);
        if (kind == MoneyAccountKind.RachunekBankowy && !string.IsNullOrWhiteSpace(iban) && !Identifiers.IsValidIban(iban))
            throw new DomainException("Nieprawidłowy numer rachunku (IBAN/NRB).");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Podaj nazwę rachunku/kasy.");
        var m = new MoneyAccount
        {
            CompanyId = _user.CompanyId,
            Kind = kind,
            Name = name.Trim(),
            LedgerAccountNumber = ledgerAccountNumber,
            Iban = iban is null ? null : Identifiers.NormalizeIban(iban),
            Currency = currency
        };
        _db.MoneyAccounts.Add(m);
        await _audit.AddAsync(_user, "RACHUNEK_UTWORZONY", "MoneyAccount", m.Id.ToString(), $"{m.Name} ({m.LedgerAccountNumber})", ct);
        await _db.SaveChangesAsync(ct);
        return m;
    }

    /// <summary>Import wyciągu. Ponowny import tego samego pliku jest blokowany; pojedyncze operacje już zaimportowane są pomijane.</summary>
    public async Task<ImportResult> ImportStatementAsync(Guid moneyAccountId, ParsedStatement parsed, string fileName, byte[] fileContent, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageBank);
        var account = await _db.MoneyAccounts.FirstAsync(m => m.Id == moneyAccountId && m.CompanyId == _user.CompanyId, ct);
        var sha = Convert.ToHexString(SHA256.HashData(fileContent));
        if (await _db.MoneyStatements.AnyAsync(s => s.MoneyAccountId == moneyAccountId && s.SourceFileSha256 == sha, ct))
            throw new DomainException("Ten plik wyciągu został już zaimportowany (identyczna suma SHA-256).");
        var warnings = new List<string>();
        if (parsed.AccountIban is { } ib && account.Iban is { } aib && Identifiers.NormalizeIban(ib) != aib)
            warnings.Add($"Numer rachunku w pliku ({ib}) różni się od rachunku {account.Name} ({aib}).");
        if (parsed.Currency != account.Currency) warnings.Add($"Waluta wyciągu {parsed.Currency} ≠ waluta rachunku {account.Currency}.");
        if (parsed.OpeningBalance is { } ob && parsed.ClosingBalance is { } cb && ob + parsed.Transactions.Sum(t => t.Amount) != cb)
            warnings.Add($"Saldo otwarcia + operacje ≠ saldo zamknięcia wyciągu (różnica {Money.Format(ob + parsed.Transactions.Sum(t => t.Amount) - cb)}).");

        var statement = new MoneyStatement
        {
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
            Number = parsed.Number ?? $"{parsed.DateFrom:yyyyMMdd}-{parsed.DateTo:yyyyMMdd}",
            DateFrom = parsed.DateFrom ?? _clock.Today,
            DateTo = parsed.DateTo ?? _clock.Today,
            OpeningBalance = parsed.OpeningBalance,
            ClosingBalance = parsed.ClosingBalance,
            SourceFileName = Path.GetFileName(fileName),
            SourceFileSha256 = sha,
            ImportedAtUtc = _clock.UtcNow,
            ImportedByUserId = _user.UserId
        };
        _db.MoneyStatements.Add(statement);
        var occurrences = new Dictionary<string, int>();
        int imported = 0, duplicates = 0;
        var existing = (await _db.MoneyTransactions.Where(t => t.MoneyAccountId == moneyAccountId && t.BookingDate >= statement.DateFrom && t.BookingDate <= statement.DateTo)
            .Select(t => t.DedupHash).ToListAsync(ct)).ToHashSet();
        foreach (var t in parsed.Transactions)
        {
            var baseHash = MoneyTransaction.ComputeDedupHash(moneyAccountId, t.BookingDate, t.Amount, t.Reference, t.CounterpartyIban, t.Title);
            var occ = occurrences.GetValueOrDefault(baseHash) + 1;
            occurrences[baseHash] = occ;
            var hash = occ == 1 ? baseHash : MoneyTransaction.ComputeDedupHash(moneyAccountId, t.BookingDate, t.Amount, (t.Reference ?? "") + "#" + occ, t.CounterpartyIban, t.Title);
            if (existing.Contains(hash)) { duplicates++; continue; }
            existing.Add(hash);
            _db.MoneyTransactions.Add(new MoneyTransaction
            {
                CompanyId = account.CompanyId,
                MoneyAccountId = account.Id,
                StatementId = statement.Id,
                BookingDate = t.BookingDate,
                ValueDate = t.ValueDate,
                Amount = t.Amount,
                Currency = t.Currency,
                CounterpartyName = t.CounterpartyName,
                CounterpartyIban = t.CounterpartyIban,
                Title = t.Title.Length > 1900 ? t.Title[..1900] : t.Title,
                BankReference = t.Reference,
                DedupHash = hash,
                CreatedAtUtc = _clock.UtcNow
            });
            imported++;
        }
        if (duplicates > 0) warnings.Add($"Pominięto {duplicates} operacji zaimportowanych wcześniej (duplikaty).");
        await _audit.AddAsync(_user, "WYCIAG_ZAIMPORTOWANY", "MoneyStatement", statement.Id.ToString(), $"{account.Name}: {statement.SourceFileName}; operacji {imported}, duplikatów {duplicates}; SHA-256 {sha}", ct);
        await _db.SaveChangesAsync(ct);
        return new ImportResult(statement.Id, imported, duplicates, warnings);
    }

    /// <summary>Propozycje dopasowania: numer dokumentu w tytule, zgodność kwoty, rachunek/NIP kontrahenta.</summary>
    public async Task<List<MatchSuggestion>> SuggestMatchesAsync(Guid transactionId, CancellationToken ct = default)
    {
        var t = await _db.MoneyTransactions.FirstAsync(x => x.Id == transactionId && x.CompanyId == _user.CompanyId, ct);
        var side = t.Amount > 0 ? OpenItemSide.Naleznosc : OpenItemSide.Zobowiazanie;
        var items = await _db.OpenItems.Where(o => o.CompanyId == _user.CompanyId && o.Side == side && o.Status != OpenItemStatus.Rozliczona).ToListAsync(ct);
        var cps = await _db.Counterparties.Where(c => c.CompanyId == _user.CompanyId).ToDictionaryAsync(c => c.Id, ct);
        var title = Normalize(t.Title);
        var amount = Math.Abs(t.Amount);
        var list = new List<MatchSuggestion>();
        foreach (var o in items)
        {
            var score = 0;
            var reasons = new List<string>();
            var cp = cps.GetValueOrDefault(o.CounterpartyId);
            var reference = Normalize(o.Reference.Split(' ')[0]);
            if (reference.Length >= 3 && title.Contains(reference)) { score += 50; reasons.Add("numer dokumentu w tytule"); }
            if (o.Remaining == amount) { score += 30; reasons.Add("kwota = pozostało do zapłaty"); }
            else if (o.Amount == amount) { score += 15; reasons.Add("kwota = wartość dokumentu"); }
            if (cp?.BankAccountIban is { } ib && t.CounterpartyIban is { } tib && Identifiers.NormalizeIban(ib) == Identifiers.NormalizeIban(tib)) { score += 30; reasons.Add("rachunek kontrahenta"); }
            if (cp?.Nip is { Length: 10 } nip && title.Contains(nip)) { score += 20; reasons.Add("NIP w tytule"); }
            if (cp is not null && t.CounterpartyName is { } n && Normalize(n).Contains(Normalize(cp.Name).Split(' ')[0])) { score += 10; reasons.Add("nazwa kontrahenta"); }
            if (score >= 30 && cp is not null)
                list.Add(new MatchSuggestion(t.Id, o.Id, o.Reference, o.CounterpartyId, cp.Name, o.Remaining, score, string.Join(", ", reasons)));
        }
        return list.OrderByDescending(s => s.Score).ThenBy(s => s.Remaining).Take(10).ToList();
    }

    /// <summary>Automatyczne dopasowanie operacji o jednoznacznej propozycji (score ≥ 80, pełna kwota). Zwraca liczbę dopasowań.</summary>
    public async Task<int> AutoMatchAsync(Guid statementId, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageBank);
        var txs = await _db.MoneyTransactions.Where(t => t.StatementId == statementId && t.Status == TransactionStatus.Nierozliczona).ToListAsync(ct);
        var matched = 0;
        var used = new HashSet<Guid>();
        foreach (var t in txs)
        {
            var s = (await SuggestMatchesAsync(t.Id, ct)).Where(x => !used.Contains(x.OpenItemId)).ToList();
            if (s.Count == 0 || s[0].Score < 80 || s[0].Remaining != Math.Abs(t.Amount)) continue;
            if (s.Count > 1 && s[1].Score == s[0].Score) continue; // niejednoznaczne
            t.Notes = Allocations.Serialize(new[] { new Allocation(s[0].OpenItemId, Math.Abs(t.Amount)) }, null);
            t.MatchedCounterpartyId = s[0].CounterpartyId;
            t.Status = TransactionStatus.Dopasowana;
            used.Add(s[0].OpenItemId);
            matched++;
        }
        await _db.SaveChangesAsync(ct);
        return matched;
    }

    /// <summary>Ręczne dopasowanie: rozliczenia z pozycjami rozrachunkowymi oraz ewentualna reszta na konto przeciwstawne (np. opłaty, odsetki).</summary>
    public async Task MatchAsync(Guid transactionId, IReadOnlyList<Allocation> allocations, string? offsetAccountNumber, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageBank);
        var t = await _db.MoneyTransactions.FirstAsync(x => x.Id == transactionId && x.CompanyId == _user.CompanyId, ct);
        if (t.Status == TransactionStatus.Zaksiegowana) throw new DomainException("Operacja jest już zaksięgowana.");
        var total = allocations.Sum(a => a.Amount);
        if (allocations.Any(a => a.Amount <= 0)) throw new DomainException("Kwoty rozliczeń muszą być dodatnie.");
        if (total > Math.Abs(t.Amount)) throw new DomainException("Suma rozliczeń przekracza kwotę operacji.");
        if (total < Math.Abs(t.Amount) && string.IsNullOrWhiteSpace(offsetAccountNumber))
            throw new DomainException($"Nierozliczona reszta {Money.Format(Math.Abs(t.Amount) - total)} — wskaż konto przeciwstawne lub dodatkowe rozrachunki.");
        Guid? cp = null;
        foreach (var a in allocations)
        {
            var item = await _db.OpenItems.FirstAsync(o => o.Id == a.OpenItemId && o.CompanyId == _user.CompanyId, ct);
            if (a.Amount > item.Remaining) throw new DomainException($"Kwota rozliczenia przekracza saldo rozrachunku {item.Reference}.");
            var expectedSide = t.Amount > 0 ? OpenItemSide.Naleznosc : OpenItemSide.Zobowiazanie;
            if (item.Side != expectedSide)
                throw new DomainException(t.Amount > 0 ? $"Wpływ rozlicza zobowiązanie {item.Reference} — dopuszczalne tylko jako zwrot (zaksięguj poleceniem księgowania)."
                                                       : $"Wypływ rozlicza należność {item.Reference} — dopuszczalne tylko jako zwrot nadpłaty (polecenie księgowania).");
            cp ??= item.CounterpartyId;
        }
        t.Notes = Allocations.Serialize(allocations, offsetAccountNumber);
        t.MatchedCounterpartyId = cp;
        t.Status = TransactionStatus.Dopasowana;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SkipAsync(Guid transactionId, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageBank);
        var t = await _db.MoneyTransactions.FirstAsync(x => x.Id == transactionId && x.CompanyId == _user.CompanyId, ct);
        if (t.Status == TransactionStatus.Zaksiegowana) throw new DomainException("Operacja jest zaksięgowana.");
        t.Status = TransactionStatus.Pominieta;
        t.Notes = "POMINIETA: " + reason;
        await _audit.AddAsync(_user, "OPERACJA_POMINIETA", "MoneyTransaction", t.Id.ToString(), reason, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Księgowanie operacji dopasowanej: Wn bank / Ma rozrachunek (wpływ) lub odwrotnie (wypływ). Dla rachunku walutowego
    /// różnica między wartością rozliczenia po kursie z dnia zapłaty a wartością rozrachunku po kursie historycznym
    /// jest księgowana jako zrealizowana różnica kursowa (art. 30 ust. 2 UoR).
    /// </summary>
    public async Task<JournalEntry> PostTransactionAsync(Guid transactionId, decimal? exchangeRate = null, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var t = await _db.MoneyTransactions.FirstAsync(x => x.Id == transactionId && x.CompanyId == _user.CompanyId, ct);
        if (t.Status == TransactionStatus.Zaksiegowana) throw new DomainException("Operacja jest już zaksięgowana.");
        if (t.Status != TransactionStatus.Dopasowana) throw new DomainException("Najpierw dopasuj operację do rozrachunków lub wskaż konto przeciwstawne.");
        var account = await _db.MoneyAccounts.FirstAsync(m => m.Id == t.MoneyAccountId, ct);
        var (allocations, offset) = Allocations.Deserialize(t.Notes);
        var posting = await new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PostingSettings.SettingKey, () => new PostingSettings(), ct);
        var foreign = account.Currency != "PLN";
        if (foreign && (exchangeRate is null or <= 0)) throw new DomainException("Podaj kurs waluty z dnia operacji (np. kurs kupna/sprzedaży banku lub średni NBP — zgodnie z polityką).");
        var rate = foreign ? exchangeRate!.Value : 1m;
        var inflow = t.Amount > 0;
        var abs = Math.Abs(t.Amount);
        var amountPln = foreign ? Money.ToPln(abs, rate) : abs;

        var lines = new List<LineInput>();
        lines.Add(inflow ? new LineInput(account.LedgerAccountNumber, amountPln, 0, null, t.Title, null, foreign ? abs : null)
                         : new LineInput(account.LedgerAccountNumber, 0, amountPln, null, t.Title, null, foreign ? abs : null));
        decimal allocatedPln = 0, fxDiff = 0;
        foreach (var a in allocations)
        {
            var item = await _db.OpenItems.FirstAsync(o => o.Id == a.OpenItemId, ct);
            if (item.Currency != account.Currency) throw new DomainException($"Rozrachunek {item.Reference} jest w walucie {item.Currency}, a rachunek w {account.Currency} — rozliczenie wielowalutowe niezaimplementowane (użyj polecenia księgowania).");
            decimal itemPln;
            if (foreign)
            {
                // wartość rozrachunku po kursie historycznym (proporcjonalnie do rozliczanej części)
                itemPln = a.Amount == item.Remaining ? item.RemainingPln : Money.Round(a.Amount / item.Amount * item.AmountPln);
                var paidPln = Money.ToPln(a.Amount, rate);
                fxDiff += inflow ? paidPln - itemPln : itemPln - paidPln;
            }
            else itemPln = a.Amount;
            allocatedPln += foreign ? Money.ToPln(a.Amount, rate) : a.Amount;
            lines.Add(inflow ? new LineInput(item.AccountNumber, 0, itemPln, item.CounterpartyId, $"Zapłata {item.Reference}", item.Id, foreign ? a.Amount : null)
                             : new LineInput(item.AccountNumber, itemPln, 0, item.CounterpartyId, $"Zapłata {item.Reference}", item.Id, foreign ? a.Amount : null));
        }
        if (fxDiff > 0) lines.Add(new LineInput(posting.FxGains, 0, fxDiff, Description: "Dodatnia różnica kursowa zrealizowana"));
        if (fxDiff < 0) lines.Add(new LineInput(posting.FxLosses, -fxDiff, 0, Description: "Ujemna różnica kursowa zrealizowana"));
        var rest = amountPln - allocatedPln;
        if (rest != 0)
        {
            if (offset is null) throw new DomainException("Brak konta przeciwstawnego dla nierozliczonej części operacji.");
            lines.Add(inflow ? new LineInput(offset, 0, rest, t.MatchedCounterpartyId, t.Title) : new LineInput(offset, rest, 0, t.MatchedCounterpartyId, t.Title));
        }

        var journal = account.Kind == MoneyAccountKind.Kasa ? "KASA" : "BANK";
        var docType = account.Kind == MoneyAccountKind.Kasa ? (inflow ? "KP" : "KW") : "WB";
        var statement = t.StatementId is { } sid ? await _db.MoneyStatements.FirstAsync(s => s.Id == sid, ct) : null;
        var docNumber = statement is not null ? $"WB {statement.Number}" : $"{docType}/{t.BookingDate:yyyyMMdd}/{t.Id.ToString()[..8]}";

        var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (account.Kind == MoneyAccountKind.Kasa && !inflow)
            {
                var (fy, _) = await new LedgerService(_db, _user, _clock).PeriodForDateAsync(t.BookingDate, ct);
                var tb = await new LedgerQueries(_db).TrialBalanceAsync(fy.Id, t.BookingDate, t.BookingDate, ct);
                var cash = tb.FirstOrDefault(x => x.Account.Number == account.LedgerAccountNumber)?.Balance ?? 0;
                if (cash - amountPln < 0) throw new DomainException($"Saldo kasy ({Money.Format(cash)}) nie pokrywa wypłaty {Money.Format(amountPln)} — saldo kasy nie może być ujemne.");
            }
            var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput(journal, t.BookingDate, statement?.DateTo ?? t.BookingDate, t.BookingDate,
                $"{(inflow ? "Wpływ" : "Wypływ")}: {t.CounterpartyName} — {t.Title}".Trim(), docType, docNumber, lines, null, account.Currency, foreign ? rate : null), ct);
            t.JournalEntryId = entry.Id;
            t.Status = TransactionStatus.Zaksiegowana;
            foreach (var al in _db.SettlementAllocations.Local.Where(x => x.JournalEntryId == entry.Id))
            {
                al.MoneyTransactionId = t.Id;
                al.Source = account.Kind == MoneyAccountKind.Kasa ? SettlementSource.Kasa : SettlementSource.Bank;
                al.ExchangeDifferencePln = fxDiff;
            }
            await _db.SaveChangesAsync(ct);
            var docs = new DocumentService(_db, _user, _clock);
            foreach (var a in allocations)
            {
                var item = await _db.OpenItems.FirstAsync(o => o.Id == a.OpenItemId, ct);
                if (item.DocumentId is { } did) await docs.RefreshPaymentStatusAsync(did, ct);
            }
            if (statement is not null && !await _db.MoneyTransactions.AnyAsync(x => x.StatementId == statement.Id && x.Status != TransactionStatus.Zaksiegowana && x.Status != TransactionStatus.Pominieta, ct))
                statement.Status = StatementStatus.Rozliczony;
            else if (statement is not null) statement.Status = StatementStatus.WTrakcieRozliczania;
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return entry;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    /// <summary>Operacja kasowa (KP/KW) wprowadzana ręcznie — tworzy operację na kasie gotową do dopasowania i księgowania.</summary>
    public async Task<MoneyTransaction> AddCashOperationAsync(Guid cashAccountId, DateOnly date, decimal amount, string title, Guid? counterpartyId, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageBank);
        var acc = await _db.MoneyAccounts.FirstAsync(m => m.Id == cashAccountId && m.CompanyId == _user.CompanyId, ct);
        if (acc.Kind != MoneyAccountKind.Kasa) throw new DomainException("Operacje ręczne dotyczą kasy.");
        if (amount == 0) throw new DomainException("Kwota nie może być zerowa.");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Podaj tytuł operacji kasowej.");
        var cpName = counterpartyId is { } cid ? (await _db.Counterparties.FirstAsync(c => c.Id == cid, ct)).Name : null;
        var seq = await new NumberingService(_db).NextAsync(_user.CompanyId, $"CASH/{acc.Id:N}/{date.Year}", ct);
        var t = new MoneyTransaction
        {
            CompanyId = acc.CompanyId,
            MoneyAccountId = acc.Id,
            BookingDate = date,
            Amount = amount,
            Currency = acc.Currency,
            CounterpartyName = cpName,
            Title = title.Trim(),
            BankReference = $"{(amount > 0 ? "KP" : "KW")}/{seq}/{date.Year}",
            MatchedCounterpartyId = counterpartyId,
            CreatedAtUtc = _clock.UtcNow
        };
        t.DedupHash = MoneyTransaction.ComputeDedupHash(acc.Id, date, amount, t.BankReference, null, t.Title);
        _db.MoneyTransactions.Add(t);
        await _db.SaveChangesAsync(ct);
        return t;
    }

    private static string Normalize(string s) => Regex.Replace(s.ToUpperInvariant(), "[^A-Z0-9/]", "");
}

/// <summary>Serializacja dopasowania operacji (przechowywane w polu notatek operacji do czasu zaksięgowania).</summary>
internal static class Allocations
{
    public static string Serialize(IEnumerable<Allocation> a, string? offset) =>
        System.Text.Json.JsonSerializer.Serialize(new Dto(a.Select(x => new Item(x.OpenItemId, x.Amount)).ToList(), offset));

    public static (List<Allocation> allocations, string? offset) Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return (new(), null);
        var d = System.Text.Json.JsonSerializer.Deserialize<Dto>(json)!;
        return (d.Items.Select(i => new Allocation(i.OpenItemId, i.Amount)).ToList(), d.Offset);
    }

    private sealed record Item(Guid OpenItemId, decimal Amount);
    private sealed record Dto(List<Item> Items, string? Offset);
}
