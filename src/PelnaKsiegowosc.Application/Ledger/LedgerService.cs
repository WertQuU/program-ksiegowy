using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;

namespace PelnaKsiegowosc.Application.Ledger;

/// <summary>Pozycja dekretu wprowadzana przez użytkownika / schemat dekretacji.</summary>
public sealed record LineInput(string AccountNumber, decimal Debit, decimal Credit, Guid? CounterpartyId = null,
    string? Description = null, Guid? OpenItemId = null, decimal? CurrencyAmount = null, CitClassification? Cit = null);

public sealed record EntryInput(
    string JournalCode,
    DateOnly OperationDate,
    DateOnly DocumentDate,
    DateOnly PostingDate,
    string Description,
    string DocumentType,
    string DocumentNumber,
    IReadOnlyList<LineInput> Lines,
    Guid? SourceDocumentId = null,
    string Currency = "PLN",
    decimal? ExchangeRate = null,
    EntryKind Kind = EntryKind.Zwykly);

/// <summary>
/// Silnik księgowy: bufor → zaksięgowanie (numeracja ciągła w roku, łańcuch skrótów, rozrachunki),
/// korekty wyłącznie przez storno i zapis korygujący. Nie zależy od interfejsu użytkownika.
/// </summary>
public sealed class LedgerService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly NumberingService _numbering;

    /// <summary>Pozwala pominąć kontrolę gotowości (polityka/plan kont) — wyłącznie dla bilansu otwarcia wprowadzanego w kreatorze i testów jednostkowych silnika.</summary>
    public bool SkipReadinessCheck { get; set; }

    public LedgerService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = new AuditService(db, clock);
        _numbering = new NumberingService(db);
    }

    public async Task<FiscalYear> FiscalYearForDateAsync(DateOnly date, CancellationToken ct = default) =>
        await _db.FiscalYears.Include(f => f.Periods).FirstOrDefaultAsync(f => f.CompanyId == _user.CompanyId && f.StartDate <= date && f.EndDate >= date, ct)
        ?? throw new DomainException($"Brak roku obrotowego obejmującego datę {date.ToPl()}.");

    public async Task<(FiscalYear year, AccountingPeriod period)> PeriodForDateAsync(DateOnly date, CancellationToken ct = default)
    {
        var fy = await FiscalYearForDateAsync(date, ct);
        var p = fy.Periods.FirstOrDefault(x => x.Contains(date)) ?? throw new DomainException($"Brak okresu dla daty {date.ToPl()}.");
        return (fy, p);
    }

    public async Task<Dictionary<string, Account>> AccountsByNumberAsync(Guid fiscalYearId, CancellationToken ct = default)
    {
        var chart = await _db.Charts.Where(c => c.FiscalYearId == fiscalYearId && c.CompanyId == _user.CompanyId)
            .OrderByDescending(c => c.Version).FirstOrDefaultAsync(ct)
                    ?? throw new DomainException("Brak planu kont dla roku obrotowego tej jednostki.");
        return await _db.Accounts.Where(a => a.ChartId == chart.Id).ToDictionaryAsync(a => a.Number, ct);
    }

    /// <summary>Tworzy zapis w buforze (edytowalny).</summary>
    public async Task<JournalEntry> CreateDraftAsync(EntryInput input, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var (fy, period) = await PeriodForDateAsync(input.PostingDate, ct);
        var accounts = await AccountsByNumberAsync(fy.Id, ct);
        var entry = new JournalEntry
        {
            CompanyId = _user.CompanyId,
            FiscalYearId = fy.Id,
            PeriodId = period.Id,
            CreatedByUserId = _user.UserId,
            CreatedAtUtc = _clock.UtcNow
        };
        Apply(entry, input, accounts);
        var validation = EntryValidator.ValidateStructure(entry, accounts.Values.ToDictionary(a => a.Id));
        validation.ThrowIfInvalid();
        await EnsureCounterpartiesBelongToCompanyAsync(entry, ct);
        _db.JournalEntries.Add(entry);
        await _audit.AddAsync(_user, "ZAPIS_BUFOR_UTWORZONY", "JournalEntry", entry.Id.ToString(), $"{entry.DocumentNumber}: {entry.Description}", ct);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Zmiana zapisu w buforze. Zapisu zaksięgowanego nie można zmienić — wymagane storno.</summary>
    public async Task<JournalEntry> UpdateDraftAsync(Guid entryId, EntryInput input, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var entry = await _db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == _user.CompanyId, ct) ?? throw new DomainException("Nie znaleziono zapisu w księgach tej jednostki.");
        if (entry.IsPosted)
            throw new DomainException("Zapis zaksięgowany nie może być modyfikowany. Użyj funkcji „Korekta (storno)” — zapis pierwotny pozostanie w księgach.");
        var (fy, period) = await PeriodForDateAsync(input.PostingDate, ct);
        var accounts = await AccountsByNumberAsync(fy.Id, ct);
        // Najpierw zweryfikuj niezależny projekt dekretu. Wcześniejsza kolejność
        // usuwała linie śledzone przez EF przed walidacją; błąd walidacji
        // pozostawiał w DbContext zmiany, które mogły zapisać się przy następnej operacji.
        var candidate = new JournalEntry
        {
            CompanyId = entry.CompanyId,
            FiscalYearId = fy.Id,
            PeriodId = period.Id
        };
        Apply(candidate, input, accounts);
        EntryValidator.ValidateStructure(candidate, accounts.Values.ToDictionary(a => a.Id)).ThrowIfInvalid();

        _db.JournalLines.RemoveRange(entry.Lines);
        entry.Lines.Clear();
        entry.FiscalYearId = fy.Id;
        entry.PeriodId = period.Id;
        Apply(entry, input, accounts);
        entry.ModifiedAtUtc = _clock.UtcNow;
        entry.ModifiedByUserId = _user.UserId;
        foreach (var l in entry.Lines) _db.Entry(l).State = EntityState.Added;
        await _audit.AddAsync(_user, "ZAPIS_BUFOR_ZMIENIONY", "JournalEntry", entry.Id.ToString(), $"{entry.DocumentNumber}: {entry.Description}", ct);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task DeleteDraftAsync(Guid entryId, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var entry = await _db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == _user.CompanyId, ct) ?? throw new DomainException("Nie znaleziono zapisu w księgach tej jednostki.");
        if (entry.IsPosted) throw new DomainException("Zapisu zaksięgowanego nie można usunąć.");
        _db.JournalEntries.Remove(entry);
        await _audit.AddAsync(_user, "ZAPIS_BUFOR_USUNIETY", "JournalEntry", entry.Id.ToString(), entry.DocumentNumber, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Zaksięgowanie zapisu z bufora.</summary>
    public async Task<JournalEntry> PostAsync(Guid entryId, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var entry = await _db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == _user.CompanyId, ct) ?? throw new DomainException("Nie znaleziono zapisu w księgach tej jednostki.");
        var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await PostInternalAsync(entry, ct);
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
        return entry;
    }

    /// <summary>Tworzy i od razu księguje zapis (używane przez moduły: dokumenty, bank, amortyzacja). Zapis do bazy wykonuje wywołujący (SaveChanges).</summary>
    public async Task<JournalEntry> CreateAndPostAsync(EntryInput input, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var (fy, period) = await PeriodForDateAsync(input.PostingDate, ct);
        var accounts = await AccountsByNumberAsync(fy.Id, ct);
        var entry = new JournalEntry
        {
            CompanyId = _user.CompanyId,
            FiscalYearId = fy.Id,
            PeriodId = period.Id,
            CreatedByUserId = _user.UserId,
            CreatedAtUtc = _clock.UtcNow
        };
        Apply(entry, input, accounts);
        await EnsureCounterpartiesBelongToCompanyAsync(entry, ct);
        _db.JournalEntries.Add(entry);
        await PostInternalAsync(entry, ct);
        return entry;
    }

    private async Task PostInternalAsync(JournalEntry entry, CancellationToken ct)
    {
        if (entry.IsPosted) throw new DomainException("Zapis jest już zaksięgowany.");
        if (entry.CompanyId != _user.CompanyId)
            throw new DomainException("Zapis księgowy należy do innej jednostki.");
        var fy = await _db.FiscalYears.Include(f => f.Periods)
            .FirstAsync(f => f.Id == entry.FiscalYearId && f.CompanyId == _user.CompanyId, ct);
        var period = fy.Periods.First(p => p.Id == entry.PeriodId);
        var chart = await _db.Charts.Where(c => c.FiscalYearId == fy.Id && c.CompanyId == _user.CompanyId)
            .OrderByDescending(c => c.Version).FirstAsync(ct);
        var accounts = await _db.Accounts.Where(a => a.ChartId == chart.Id).ToDictionaryAsync(a => a.Id, ct);

        if (!SkipReadinessCheck && entry.Kind != EntryKind.BilansOtwarcia) await EnsureReadyForPostingAsync(chart.Id, ct);

        var v = EntryValidator.ValidateForPosting(entry, new EntryValidator.Context(fy, period, accounts));
        v.ThrowIfInvalid();
        await EnsureCounterpartiesBelongToCompanyAsync(entry, ct);

        // numeracja ciągła w roku obrotowym (art. 14 ust. 2 i art. 23 ust. 3 UoR)
        var seq = await _numbering.NextAsync(entry.CompanyId, $"JE/{fy.Id:N}", ct);
        var jn = await _numbering.NextAsync(entry.CompanyId, $"JN/{entry.JournalCode}/{fy.Id:N}", ct);
        entry.SequenceNumber = seq;
        entry.JournalNumber = $"{entry.JournalCode}/{fy.StartDate.Year}/{jn:000000}";

        await ApplyOpenItemsAsync(entry, accounts, ct);
        // Zapis z bufora: powiązania pozycji z rozrachunkami utrwalane są, zanim zapis otrzyma status „zaksięgowany”
        // (wyzwalacze bazy blokują zmianę pozycji zapisu zaksięgowanego). Całość wykonywana jest w jednej transakcji.
        if (_db.Entry(entry).State != EntityState.Added) await _db.SaveChangesAsync(ct);

        var prev = seq == 1 ? null : await PreviousHashAsync(fy.Id, seq - 1, ct);
        entry.Status = EntryStatus.Zaksiegowany;
        entry.PostedByUserId = _user.UserId;
        entry.PostedAtUtc = TruncateToMicroseconds(_clock.UtcNow);
        entry.PreviousHash = prev;
        entry.Hash = entry.ComputeHash();
        await _audit.AddAsync(_user, "ZAPIS_ZAKSIEGOWANY", "JournalEntry", entry.Id.ToString(),
            $"{entry.JournalNumber} poz.{seq}; {entry.DocumentNumber}; Wn={Money.Xml(entry.TotalDebit)} Ma={Money.Xml(entry.TotalCredit)}", ct);
    }

    /// <summary>
    /// Wszystkie identyfikatory kontrahentów w dekrecie muszą należeć do jednostki.
    /// Uwzględniamy też nowo utworzonych kontrahentów śledzonych przez bieżący
    /// kontekst (np. faktura i kontrahent tworzone w jednej transakcji).
    /// </summary>
    private async Task EnsureCounterpartiesBelongToCompanyAsync(JournalEntry entry, CancellationToken ct)
    {
        var ids = entry.Lines.Where(l => l.CounterpartyId.HasValue)
            .Select(l => l.CounterpartyId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;

        var local = _db.Counterparties.Local
            .Where(c => c.CompanyId == entry.CompanyId && ids.Contains(c.Id) &&
                        _db.Entry(c).State != EntityState.Deleted)
            .Select(c => c.Id).ToHashSet();
        var remaining = ids.Where(id => !local.Contains(id)).ToArray();
        if (remaining.Length == 0) return;
        var existing = await _db.Counterparties.AsNoTracking()
            .Where(c => c.CompanyId == entry.CompanyId && remaining.Contains(c.Id))
            .Select(c => c.Id).ToListAsync(ct);
        if (existing.Count != remaining.Length)
            throw new DomainException("Dekret wskazuje kontrahenta nieistniejącego lub należącego do innej jednostki.");
    }

    private static DateTime TruncateToMicroseconds(DateTime dt) => new(dt.Ticks - dt.Ticks % 10, DateTimeKind.Utc);

    private async Task<string?> PreviousHashAsync(Guid fiscalYearId, long seq, CancellationToken ct)
    {
        var local = _db.JournalEntries.Local.FirstOrDefault(e => e.FiscalYearId == fiscalYearId && e.SequenceNumber == seq && e.IsPosted);
        if (local is not null) return local.Hash;
        return await _db.JournalEntries.Where(e => e.FiscalYearId == fiscalYearId && e.SequenceNumber == seq).Select(e => e.Hash).FirstOrDefaultAsync(ct)
               ?? throw new DomainException($"Przerwana ciągłość numeracji dziennika (brak pozycji {seq}). Uruchom kontrolę integralności.");
    }

    /// <summary>
    /// Rozrachunki: pozycja na koncie rozrachunkowym bez wskazanej pozycji rozrachunkowej otwiera nową należność (Wn)
    /// lub zobowiązanie (Ma). Pozycja ze wskazaną pozycją rozrachunkową: po stronie pozycji — zmienia jej kwotę (korekta),
    /// po stronie przeciwnej — rozlicza ją (zapłata, kompensata).
    /// </summary>
    private async Task ApplyOpenItemsAsync(JournalEntry entry, IReadOnlyDictionary<Guid, Account> accounts, CancellationToken ct)
    {
        foreach (var l in entry.Lines.OrderBy(x => x.LineNo))
        {
            var acc = accounts[l.AccountId];
            if (!acc.IsSettlementAccount) continue;
            var signed = l.Debit - l.Credit; // >0 Wn, <0 Ma
            // BO przenosi istniejące pozycje rozrachunkowe z poprzedniego roku — nie zmienia ich stanu
            if (entry.Kind == EntryKind.BilansOtwarcia && l.OpenItemId is not null) continue;
            if (l.OpenItemId is null)
            {
                if (l.Debit < 0 || l.Credit < 0)
                    throw new DomainException($"Pozycja {l.LineNo}: storno na koncie rozrachunkowym {acc.Number} wymaga wskazania pozycji rozrachunkowej.");
                var item = new OpenItem
                {
                    CompanyId = entry.CompanyId,
                    AccountId = acc.Id,
                    AccountNumber = acc.Number,
                    CounterpartyId = l.CounterpartyId!.Value,
                    DocumentId = entry.SourceDocumentId,
                    JournalEntryId = entry.Id,
                    Side = signed > 0 ? OpenItemSide.Naleznosc : OpenItemSide.Zobowiazanie,
                    Reference = entry.DocumentNumber,
                    DocumentDate = entry.DocumentDate,
                    DueDate = null,
                    Currency = entry.Currency,
                    Amount = l.CurrencyAmount is { } ca && entry.Currency != "PLN" ? Math.Abs(ca) : Math.Abs(signed),
                    AmountPln = Math.Abs(signed),
                    Description = l.Description ?? entry.Description
                };
                _db.OpenItems.Add(item);
                l.OpenItemId = item.Id;
            }
            else
            {
                var item = _db.OpenItems.Local.FirstOrDefault(o => o.Id == l.OpenItemId && o.CompanyId == entry.CompanyId)
                    ?? await _db.OpenItems.FirstOrDefaultAsync(o => o.Id == l.OpenItemId && o.CompanyId == entry.CompanyId, ct)
                    ?? throw new DomainException($"Pozycja {l.LineNo}: rozrachunek nie należy do tej jednostki lub nie istnieje.");
                if (item.AccountNumber != acc.Number) throw new DomainException($"Pozycja {l.LineNo}: rozrachunek {item.Reference} dotyczy innego konta.");
                if (item.CounterpartyId != l.CounterpartyId) throw new DomainException($"Pozycja {l.LineNo}: rozrachunek {item.Reference} dotyczy innego kontrahenta.");
                var sameSide = item.Side == OpenItemSide.Naleznosc ? l.Debit != 0 : l.Credit != 0;
                var effectPln = item.Side == OpenItemSide.Naleznosc ? signed : -signed; // dodatni = zwiększenie salda pozycji
                var ratio = item.AmountPln == 0 ? 1m : item.Amount / item.AmountPln;
                var effectCur = l.CurrencyAmount is { } cur && item.Currency != "PLN" ? (sameSide ? cur : -cur) : Money.Round(effectPln * (item.Currency == "PLN" ? 1m : ratio));
                if (sameSide)
                {
                    item.AmountPln += effectPln;
                    item.Amount += effectCur;
                    if (item.AmountPln < item.SettledAmountPln) throw new DomainException($"Pozycja {l.LineNo}: korekta zmniejsza rozrachunek {item.Reference} poniżej kwoty już rozliczonej.");
                    item.RefreshStatus();
                }
                else
                {
                    // rozliczenie (wartość ujemna efektu = spłata)
                    var settlePln = -effectPln;
                    var settleCur = item.Currency == "PLN" ? settlePln : (l.CurrencyAmount is { } c2 ? Math.Abs(c2) * Math.Sign(settlePln) : Money.Round(settlePln * ratio));
                    if (settlePln > 0 && settleCur > item.Remaining)
                        throw new DomainException($"Pozycja {l.LineNo}: kwota rozliczenia przekracza saldo rozrachunku {item.Reference} ({Money.Format(item.Remaining)} {item.Currency}).");
                    item.SettledAmount += settleCur;
                    item.SettledAmountPln += settlePln;
                    if (item.SettledAmount < 0) throw new DomainException("Storno rozliczenia przekracza kwotę rozliczoną.");
                    item.RefreshStatus();
                    _db.SettlementAllocations.Add(new SettlementAllocation
                    {
                        CompanyId = entry.CompanyId,
                        OpenItemId = item.Id,
                        Source = entry.JournalCode switch { "BANK" => SettlementSource.Bank, "KASA" => SettlementSource.Kasa, _ => SettlementSource.PolecenieKsiegowania },
                        JournalEntryId = entry.Id,
                        Date = entry.OperationDate,
                        Amount = settleCur,
                        AmountPln = settlePln,
                        UserId = _user.UserId,
                        CreatedAtUtc = _clock.UtcNow,
                        IsReversed = entry.Kind == EntryKind.Storno
                    });
                }
            }
        }
    }

    /// <summary>Storno zapisu zaksięgowanego (metoda wg polityki rachunkowości). Zapis pierwotny pozostaje w księgach.</summary>
    public async Task<JournalEntry> ReverseAsync(Guid entryId, string reason, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        _user.Require(Permission.CorrectEntries);
        var original = await _db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == _user.CompanyId, ct) ?? throw new DomainException("Nie znaleziono zapisu w księgach tej jednostki.");
        var reversal = await BuildAndPostReversalAsync(original, reason, postingDate, ct);
        await _db.SaveChangesAsync(ct);
        return reversal;
    }

    internal async Task<JournalEntry> BuildAndPostReversalAsync(JournalEntry original, string reason, DateOnly? postingDate, CancellationToken ct)
    {
        var policy = await _db.AccountingPolicies.Where(p => p.CompanyId == original.CompanyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct);
        var method = policy?.CorrectionMethod ?? CorrectionMethod.StornoCzerwone;
        var date = postingDate ?? await DefaultCorrectionDateAsync(original, ct);
        var (fy, period) = await PeriodForDateAsync(date, ct);
        var reversal = EntryValidator.BuildReversal(original, method, date, period.Id, fy.Id, reason, _user.UserId, _clock.UtcNow);
        if (fy.Id != original.FiscalYearId)
        {
            // storno w kolejnym roku — konta mapowane po numerze na plan kont nowego roku
            var accounts = await AccountsByNumberAsync(fy.Id, ct);
            foreach (var l in reversal.Lines)
                l.AccountId = accounts.TryGetValue(l.AccountNumber, out var a) ? a.Id : throw new DomainException($"Brak konta {l.AccountNumber} w planie kont roku {fy.Name}.");
        }
        _db.JournalEntries.Add(reversal);
        await PostInternalAsync(reversal, ct);
        original.ReversedByEntryId = reversal.Id;
        await _audit.AddAsync(_user, "ZAPIS_STORNOWANY", "JournalEntry", original.Id.ToString(), $"{original.JournalNumber} → {reversal.JournalNumber}; przyczyna: {reason}", ct);
        return reversal;
    }

    /// <summary>Korekta: storno zapisu pierwotnego i zaksięgowanie zapisu poprawnego (historia zachowana).</summary>
    public async Task<(JournalEntry reversal, JournalEntry corrected)> CorrectAsync(Guid entryId, string reason, EntryInput corrected, CancellationToken ct = default)
    {
        _user.Require(Permission.CorrectEntries);
        var original = await _db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == _user.CompanyId, ct) ?? throw new DomainException("Nie znaleziono zapisu w księgach tej jednostki.");
        var reversal = await BuildAndPostReversalAsync(original, reason, corrected.PostingDate, ct);
        var newEntry = await CreateAndPostAsync(corrected with { Kind = EntryKind.Korekta }, ct);
        newEntry.CorrectsEntryId = original.Id;
        newEntry.CorrectionReason = reason;
        newEntry.Hash = newEntry.ComputeHash();
        await _db.SaveChangesAsync(ct);
        return (reversal, newEntry);
    }

    private async Task<DateOnly> DefaultCorrectionDateAsync(JournalEntry original, CancellationToken ct)
    {
        var period = await _db.Periods.FirstAsync(p => p.Id == original.PeriodId, ct);
        if (period.Status == PeriodStatus.Otwarty) return original.PostingDate;
        return _clock.Today;
    }

    /// <summary>
    /// Warunek rozpoczęcia księgowania: zatwierdzona polityka rachunkowości i poprawny plan kont
    /// (wymóg kreatora — „przed pierwszym księgowaniem”).
    /// </summary>
    public async Task EnsureReadyForPostingAsync(Guid chartId, CancellationToken ct = default)
    {
        var policy = await _db.AccountingPolicies.Where(p => p.CompanyId == _user.CompanyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct);
        if (policy is null) throw new DomainException("Brak polityki rachunkowości — księgowanie zablokowane. Uzupełnij i zatwierdź politykę (Jednostka i polityka → Polityka rachunkowości).");
        var pv = policy.Validate();
        if (!pv.IsValid) throw new DomainException("Księgowanie zablokowane — polityka rachunkowości jest niekompletna:" + Environment.NewLine + string.Join(Environment.NewLine, pv.Errors));
        var chart = await _db.Charts.FirstAsync(c => c.Id == chartId && c.CompanyId == _user.CompanyId, ct);
        if (!chart.IsApproved) throw new DomainException("Księgowanie zablokowane — plan kont nie został zatwierdzony (Plan kont → Zatwierdź).");
    }

    private static void Apply(JournalEntry entry, EntryInput input, IReadOnlyDictionary<string, Account> accounts)
    {
        entry.JournalCode = input.JournalCode;
        entry.Kind = input.Kind;
        entry.OperationDate = input.OperationDate;
        entry.DocumentDate = input.DocumentDate;
        entry.PostingDate = input.PostingDate;
        entry.Description = input.Description.Trim();
        entry.DocumentType = input.DocumentType;
        entry.DocumentNumber = input.DocumentNumber.Trim();
        entry.SourceDocumentId = input.SourceDocumentId;
        entry.Currency = input.Currency;
        entry.ExchangeRate = input.Currency == "PLN" ? null : input.ExchangeRate;
        var n = 1;
        foreach (var li in input.Lines)
        {
            if (!accounts.TryGetValue(li.AccountNumber.Trim(), out var acc))
                throw new DomainException($"Pozycja {n}: konto {li.AccountNumber} nie istnieje w planie kont.");
            entry.Lines.Add(new JournalLine
            {
                EntryId = entry.Id,
                LineNo = n++,
                AccountId = acc.Id,
                AccountNumber = acc.Number,
                Debit = li.Debit,
                Credit = li.Credit,
                CounterpartyId = li.CounterpartyId,
                Description = li.Description,
                OpenItemId = li.OpenItemId,
                CurrencyAmount = li.CurrencyAmount,
                CitClassification = li.Cit
            });
        }
    }
}
