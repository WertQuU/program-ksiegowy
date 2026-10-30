using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;

namespace PelnaKsiegowosc.Application.Settlements;

/// <summary>Progi odpisów aktualizujących należności (polityka rachunkowości): procent odpisu po przekroczeniu liczby dni zwłoki.</summary>
public sealed record AllowancePolicy(int DaysPartial = 180, decimal PercentPartial = 50m, int DaysFull = 365, decimal PercentFull = 100m)
{
    public const string SettingsKey = "odpisy.progi";
    public static AllowancePolicy Default => new();

    public decimal PercentFor(int daysOverdue) => daysOverdue > DaysFull ? PercentFull : daysOverdue > DaysPartial ? PercentPartial : 0m;
}

/// <summary>Propozycja odpisu dla należności: wymagany stan odpisu i różnica do zaksięgowania (dodatnia — utworzenie, ujemna — rozwiązanie).</summary>
public sealed record AllowanceProposal(Guid OpenItemId, string Counterparty, string Reference, DateOnly? DueDate, int DaysOverdue, decimal Remaining,
    decimal Percent, decimal Required, decimal Existing, decimal Change);

public sealed record HolidayProvisionRow(Guid EmployeeId, string Employee, decimal UnusedDays, decimal DailyRate, decimal Amount);

/// <summary>
/// Wycena należności i rezerwy na koniec okresu: odpisy aktualizujące należności według wiekowania (art. 35b UoR),
/// spisanie należności nieściągalnych, rezerwy na zobowiązania (art. 35d UoR) z wyliczeniem rezerwy urlopowej.
/// Odpisy i rezerwy nie są kosztem podatkowym w chwili utworzenia — zapisy mają klasyfikację NKUP, a ich odwrócenie
/// (wykorzystanie, rozwiązanie) zmniejsza koszty NKUP, więc uzgodnienie CIT uwzględnia je automatycznie.
/// </summary>
public sealed class ValuationService
{
    public const string AllowanceAccount = "290";
    public const string AllowanceCost = "761-02";
    public const string AllowanceRelease = "760-04";
    public const string WriteOffCost = "761-03";
    public const string ProvisionAccount = "840";
    public const string EmployeeProvisionAccount = "843";

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public ValuationService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public Task<AllowancePolicy> PolicyAsync(CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync(_user.CompanyId, AllowancePolicy.SettingsKey, () => AllowancePolicy.Default, ct);

    public async Task SetPolicyAsync(AllowancePolicy p, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        if (p.DaysPartial < 0 || p.DaysFull < p.DaysPartial || p.PercentPartial is < 0 or > 100 || p.PercentFull is < 0 or > 100)
            throw new DomainException("Nieprawidłowe progi odpisów (dni rosnąco, procent 0–100).");
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, AllowancePolicy.SettingsKey, p, ct);
    }

    public async Task<decimal> ExistingAllowanceAsync(Guid openItemId, CancellationToken ct = default) =>
        (await _db.ReceivableAllowances.AsNoTracking().Where(a => a.OpenItemId == openItemId).Select(a => a.Amount).ToListAsync(ct)).Sum();

    /// <summary>Propozycje odpisów na dzień: należności przeterminowane ponad progi oraz odpisy do rozwiązania (spłata, zmiana stanu).</summary>
    public async Task<List<AllowanceProposal>> ProposeAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var policy = await PolicyAsync(ct);
        var existing = (await _db.ReceivableAllowances.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId && a.Date <= asOf).ToListAsync(ct))
            .GroupBy(a => a.OpenItemId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var items = await _db.OpenItems.AsNoTracking()
            .Where(o => o.CompanyId == _user.CompanyId && o.Side == OpenItemSide.Naleznosc && o.DocumentDate <= asOf)
            .ToListAsync(ct);
        items = items.Where(o => o.Status != OpenItemStatus.Rozliczona || existing.ContainsKey(o.Id)).ToList();
        var names = await _db.Counterparties.AsNoTracking().Where(c => c.CompanyId == _user.CompanyId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var result = new List<AllowanceProposal>();
        foreach (var o in items)
        {
            var days = o.DaysOverdue(asOf);
            var pct = policy.PercentFor(days);
            var required = Money.Round(Math.Max(0, o.RemainingPln) * pct / 100m);
            var have = existing.GetValueOrDefault(o.Id);
            if (required == 0 && have == 0) continue;
            result.Add(new AllowanceProposal(o.Id, names.GetValueOrDefault(o.CounterpartyId, ""), o.Reference, o.DueDate, days, o.RemainingPln, pct, required, have, required - have));
        }
        return result.OrderByDescending(r => r.DaysOverdue).ToList();
    }

    /// <summary>Księguje zmianę odpisu (dodatnia — utworzenie, ujemna — rozwiązanie) jednym zapisem PK.</summary>
    public async Task<ReceivableAllowance?> PostAllowanceAsync(Guid openItemId, decimal change, DateOnly date, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        change = Money.Round(change);
        if (change == 0) return null;
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Podaj uzasadnienie odpisu (np. wiekowanie, upadłość dłużnika).");
        var item = await _db.OpenItems.AsNoTracking().FirstAsync(o => o.Id == openItemId && o.CompanyId == _user.CompanyId, ct);
        if (item.Side != OpenItemSide.Naleznosc) throw new DomainException("Odpisy aktualizujące dotyczą należności.");
        var have = await ExistingAllowanceAsync(openItemId, ct);
        if (have + change < 0) throw new DomainException($"Rozwiązanie przekracza istniejący odpis ({Money.Format(have)}).");
        if (have + change > Math.Max(0, item.RemainingPln)) throw new DomainException($"Odpis nie może przekroczyć kwoty należności pozostałej do zapłaty ({Money.Format(item.RemainingPln)}).");
        await EnsureAccountsAsync(date, ct, change > 0 ? AllowanceCost : AllowanceRelease, AllowanceAccount);
        var amount = Math.Abs(change);
        var lines = change > 0
            ? new List<LineInput> { new(AllowanceCost, amount, 0, item.CounterpartyId, $"Odpis aktualizujący {item.Reference}"), new(AllowanceAccount, 0, amount, item.CounterpartyId) }
            : new List<LineInput> { new(AllowanceAccount, amount, 0, item.CounterpartyId), new(AllowanceRelease, 0, amount, item.CounterpartyId, $"Rozwiązanie odpisu {item.Reference}") };
        var number = await NextNumberAsync("ODP", date, ct);
        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date,
            $"{(change > 0 ? "Odpis aktualizujący" : "Rozwiązanie odpisu")} należności {item.Reference}: {reason.Trim()}", "PK", number, lines), ct);
        var a = new ReceivableAllowance
        {
            CompanyId = _user.CompanyId, OpenItemId = openItemId, Date = date, Amount = change, Reason = reason.Trim(),
            JournalEntryId = entry.Id, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        };
        _db.ReceivableAllowances.Add(a);
        await new AuditService(_db, _clock).AddAsync(_user, "ODPIS_NALEZNOSCI", "ReceivableAllowance", a.Id.ToString(), $"{item.Reference}; {Money.Xml(change)}; {reason}", ct);
        await _db.SaveChangesAsync(ct);
        return a;
    }

    /// <summary>Księguje wszystkie propozycje na dzień (zamknięcie miesiąca / roku).</summary>
    public async Task<int> PostProposalsAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var p in (await ProposeAsync(asOf, ct)).Where(p => p.Change != 0))
        {
            await PostAllowanceAsync(p.OpenItemId, p.Change, asOf,
                p.Change > 0 ? $"wiekowanie: {p.DaysOverdue} dni po terminie, odpis {p.Percent:0.##}%" : "należność spłacona lub zmniejszone ryzyko", ct);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Spisanie należności nieściągalnej / przedawnionej / umorzonej: wykorzystanie odpisu (Wn 290), nieobjęta odpisem część
    /// w pozostałe koszty operacyjne (Wn 761-03), Ma konto rozrachunku — rozrachunek zostaje rozliczony.
    /// </summary>
    public async Task WriteOffAsync(Guid openItemId, DateOnly date, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Podaj podstawę spisania (np. postanowienie o nieściągalności, przedawnienie, umorzenie).");
        var item = await _db.OpenItems.AsNoTracking().FirstAsync(o => o.Id == openItemId && o.CompanyId == _user.CompanyId, ct);
        if (item.Side != OpenItemSide.Naleznosc || item.Status == OpenItemStatus.Rozliczona) throw new DomainException("Spisać można nierozliczoną należność.");
        if (item.Currency != "PLN") throw new DomainException("Spisanie należności walutowej wykonaj poleceniem księgowania (różnice kursowe).");
        var remaining = item.RemainingPln;
        var have = Math.Min(await ExistingAllowanceAsync(openItemId, ct), remaining);
        await EnsureAccountsAsync(date, ct, AllowanceAccount, WriteOffCost);
        var lines = new List<LineInput>();
        if (have > 0) lines.Add(new(AllowanceAccount, have, 0, item.CounterpartyId, "Wykorzystanie odpisu aktualizującego"));
        if (remaining - have > 0) lines.Add(new(WriteOffCost, remaining - have, 0, item.CounterpartyId, "Spisana należność nieobjęta odpisem"));
        lines.Add(new(item.AccountNumber, 0, remaining, item.CounterpartyId, $"Spisanie {item.Reference}", item.Id));
        var number = await NextNumberAsync("SPN", date, ct);
        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date,
            $"Spisanie należności {item.Reference}: {reason.Trim()}", "PK", number, lines), ct);
        if (have > 0)
            _db.ReceivableAllowances.Add(new ReceivableAllowance
            {
                CompanyId = _user.CompanyId, OpenItemId = openItemId, Date = date, Amount = -have, Reason = "Wykorzystanie przy spisaniu: " + reason.Trim(),
                JournalEntryId = entry.Id, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
            });
        await new AuditService(_db, _clock).AddAsync(_user, "SPISANIE_NALEZNOSCI", "OpenItem", item.Id.ToString(), $"{item.Reference}; {Money.Xml(remaining)}; {reason}", ct);
        await _db.SaveChangesAsync(ct);
        if (item.DocumentId is { } d) await new Documents.DocumentService(_db, _user, _clock).RefreshPaymentStatusAsync(d, ct);
    }

    public static string DefaultProvisionAccount(ProvisionKind kind) =>
        kind is ProvisionKind.Urlopowa or ProvisionKind.Emerytalna ? EmployeeProvisionAccount : ProvisionAccount;

    public static string DefaultCostAccount(ProvisionKind kind) => kind switch
    {
        ProvisionKind.Urlopowa => "404",
        ProvisionKind.Emerytalna => "405",
        ProvisionKind.NaKoszty => "402",
        _ => "761-03"
    };

    /// <summary>Utworzenie rezerwy: Wn konto kosztów / Ma 840 (843). Zapis kosztu oznaczony jako NKUP (art. 16 ust. 1 pkt 27 ustawy o CIT).</summary>
    public async Task<Provision> CreateProvisionAsync(ProvisionKind kind, string description, decimal amount, DateOnly date, string? costAccount = null, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        amount = Money.Round(amount);
        if (amount <= 0) throw new DomainException("Kwota rezerwy musi być dodatnia.");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("Opisz rezerwę (tytuł i sposób wyliczenia).");
        var account = DefaultProvisionAccount(kind);
        var cost = string.IsNullOrWhiteSpace(costAccount) ? DefaultCostAccount(kind) : costAccount.Trim();
        await EnsureAccountsAsync(date, ct, account);
        var number = await NextNumberAsync("REZ", date, ct);
        var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date,
            $"Utworzenie rezerwy: {description.Trim()}", "PK", number,
            new List<LineInput> { new(cost, amount, 0, Description: "Rezerwa — " + description.Trim(), Cit: CitClassification.KosztNiepodatkowy), new(account, 0, amount) }), ct);
        var p = new Provision
        {
            CompanyId = _user.CompanyId, Kind = kind, Description = description.Trim(), Date = date, Amount = amount, AccountNumber = account,
            CostAccountNumber = cost, JournalEntryId = entry.Id, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        };
        _db.Provisions.Add(p);
        await new AuditService(_db, _clock).AddAsync(_user, "REZERWA_UTWORZENIE", "Provision", p.Id.ToString(), $"{kind}; {Money.Xml(amount)}; {description}", ct);
        await _db.SaveChangesAsync(ct);
        return p;
    }

    /// <summary>
    /// Wykorzystanie (rzeczywisty koszt poniesiono i ujęto w księgach) lub rozwiązanie (rezerwa zbędna) — Wn 840/843 / Ma konto kosztów,
    /// tak by koszt nie był ujęty dwukrotnie. Zapis Ma na koncie kosztów z klasyfikacją NKUP odwraca koszt niepodatkowy (rzeczywisty wydatek jest kosztem podatkowym przy poniesieniu).
    /// </summary>
    public async Task UseProvisionAsync(Guid provisionId, decimal amount, DateOnly date, bool release, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        amount = Money.Round(amount);
        var p = await _db.Provisions.FirstAsync(x => x.Id == provisionId && x.CompanyId == _user.CompanyId, ct);
        if (amount <= 0 || amount > p.Remaining) throw new DomainException($"Kwota musi być dodatnia i nie większa niż pozostała rezerwa ({Money.Format(p.Remaining)}).");
        if (date < p.Date) throw new DomainException("Data wykorzystania nie może być wcześniejsza niż data utworzenia rezerwy.");
        var number = await NextNumberAsync("REZ", date, ct);
        await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date,
            $"{(release ? "Rozwiązanie" : "Wykorzystanie")} rezerwy: {p.Description}", "PK", number,
            new List<LineInput> { new(p.AccountNumber, amount, 0), new(p.CostAccountNumber, 0, amount, Description: "Rezerwa — " + p.Description, Cit: CitClassification.KosztNiepodatkowy) }), ct);
        if (release) p.Released += amount;
        else p.Used += amount;
        await new AuditService(_db, _clock).AddAsync(_user, release ? "REZERWA_ROZWIAZANIE" : "REZERWA_WYKORZYSTANIE", "Provision", p.Id.ToString(), $"{Money.Xml(amount)}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<Provision>> ProvisionsAsync(CancellationToken ct = default) =>
        _db.Provisions.AsNoTracking().Where(p => p.CompanyId == _user.CompanyId).OrderByDescending(p => p.Date).ToListAsync(ct);

    /// <summary>Rezerwa urlopowa z ewidencji kadrowej — pozostały urlop każdego pracownika na koniec roku.</summary>
    public async Task<List<HolidayProvisionRow>> HolidayProvisionFromLeaveAsync(int year, CancellationToken ct = default)
    {
        var balances = await new HrService(_db, _user, _clock).LeaveBalancesAsync(year, ct);
        return await HolidayProvisionAsync(balances.Where(b => b.Remaining > 0).ToDictionary(b => b.EmployeeId, b => b.Remaining), year, ct);
    }

    /// <summary>
    /// Rezerwa na niewykorzystane urlopy: dni × (wynagrodzenie + składki pracodawcy) / współczynnik urlopowy
    /// (średnia liczba dni roboczych w miesiącu roku). Liczbę zaległych dni podaje użytkownik (ewidencja urlopów).
    /// </summary>
    public async Task<List<HolidayProvisionRow>> HolidayProvisionAsync(IReadOnlyDictionary<Guid, decimal> unusedDays, int year, CancellationToken ct = default)
    {
        var coefficient = Enumerable.Range(1, 12).Sum(m => PolishHolidays.WorkingDays(year, m)) / 12m;
        var rate = await new PayrollService(_db, _user, _clock).AccidentRateAsync(ct);
        var employees = await _db.Employees.AsNoTracking().Where(e => e.CompanyId == _user.CompanyId && e.ContractType == ContractType.UmowaOPrace).ToListAsync(ct);
        var rows = new List<HolidayProvisionRow>();
        foreach (var e in employees.OrderBy(e => e.LastName))
        {
            if (!unusedDays.TryGetValue(e.Id, out var days) || days <= 0) continue;
            var item = PayrollCalculator.Compute(e, e.Gross, new PayrollYearToDate(), PayrollRules.Y2026, rate);
            var daily = Math.Round((e.Gross + item.EmployerCost) / coefficient, 2, MidpointRounding.AwayFromZero);
            rows.Add(new HolidayProvisionRow(e.Id, $"{e.LastName} {e.FirstName}", days, daily, Money.Round(daily * days)));
        }
        return rows;
    }

    /// <summary>Dodaje brakujące konta z szablonu (plany kont utworzone przed wprowadzeniem modułu).</summary>
    internal async Task EnsureAccountsAsync(DateOnly date, CancellationToken ct, params string[] numbers)
    {
        var ledger = new LedgerService(_db, _user, _clock);
        var fy = await ledger.FiscalYearForDateAsync(date, ct);
        var existing = await ledger.AccountsByNumberAsync(fy.Id, ct);
        var chart = await _db.Charts.Where(c => c.FiscalYearId == fy.Id).OrderByDescending(c => c.Version).FirstAsync(ct);
        foreach (var n in numbers.Where(n => !existing.ContainsKey(n)))
        {
            var t = ChartTemplate.Accounts.FirstOrDefault(a => a.Number == n) ?? throw new DomainException($"Brak konta {n} w planie kont.");
            await new ChartService(_db, _user, _clock).AddAccountAsync(chart.Id, t.Number, t.Name, t.Type, t.BalanceDebitLine, t.BalanceCreditLine, t.ProfitLossLine, t.Settlement, t.Cit, ct);
        }
    }

    private async Task<string> NextNumberAsync(string prefix, DateOnly date, CancellationToken ct)
    {
        var n = await new NumberingService(_db).NextAsync(_user.CompanyId, $"{prefix}/{date.Year}", ct);
        return $"{prefix}/{n}/{date.Year}";
    }
}
