using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Cit;

public sealed record CitReconciliationRow(string Item, decimal Amount, string Basis);

public sealed class CitReconciliation
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public decimal BookRevenue { get; set; }
    public decimal BookCosts { get; set; }
    public decimal BookResultBeforeTax { get; set; }
    public List<CitReconciliationRow> Adjustments { get; } = new();
    public decimal TaxRevenue { get; set; }
    public decimal TaxCosts { get; set; }
    public decimal Deductions { get; set; }
    /// <summary>Łącznie: dochód z innych źródeł + dochód z zysków kapitałowych (podatek = suma podatków obu źródeł).</summary>
    public CitComputation Computation { get; set; } = null!;
    /// <summary>Źródło „inne niż zyski kapitałowe” (stawka 9% lub 19%).</summary>
    public CitComputation Operating { get; set; } = null!;
    /// <summary>Zyski kapitałowe (art. 7b) — odrębne źródło, zawsze 19%; strata z jednego źródła nie pomniejsza dochodu z drugiego.</summary>
    public CitComputation CapitalGains { get; set; } = null!;
    public ReducedRateDecision Rate { get; set; } = null!;
    /// <summary>Części RPD (JPK_KR_PD) wyliczone z tego samego uzgodnienia.</summary>
    public CitRpd Rpd { get; set; } = null!;
    /// <summary>Korekty z art. 18f wyliczone dla okresu (zapisywane w historii przy zapisie zaliczki).</summary>
    public List<CitLiabilityAdjustment> LiabilityAdjustments { get; } = new();
    public bool SmallTaxpayerEvidenceMissing { get; set; }
    public List<string> Warnings { get; } = new();
    /// <summary>Art. 15c: nadwyżka kosztów finansowania dłużnego roku (inne źródła / zyski kapitałowe) — do rejestru nadwyżek.</summary>
    public decimal DebtFinancingExcessOperating { get; set; }
    public decimal DebtFinancingExcessCapital { get; set; }
    /// <summary>Art. 15c ust. 18: wolny limit roku na zaliczenie nadwyżek z lat ubiegłych (limit − koszty finansowania netto roku).</summary>
    public decimal DebtFinancingHeadroom { get; set; }
    public List<(string Account, string Name, decimal Amount)> ToAnalyze { get; } = new();
}

/// <summary>
/// CIT: klasyfikacja przychodów/kosztów wg kont (KUP/NKUP/do analizy), korekty manualne, amortyzacja podatkowa
/// zamiast bilansowej, zaliczki narastająco, uzgodnienie wyniku bilansowego z podatkowym.
/// Wyliczenie NIE jest ostatecznym rozliczeniem podatkowym — wymaga weryfikacji (pozycje „do analizy” są sygnalizowane).
/// </summary>
public sealed class CitService
{
    public const string ExclusionKey = "cit.wylaczenie9";
    /// <summary>JPK_CIT od 2025 r.: podatkowa grupa kapitałowa albo przychody w poprzednim roku ponad 50 mln euro.</summary>
    public const string JpkCit2025Key = "cit.jpkCitOd2025";
    /// <summary>Kurs EUR NBP z pierwszego dnia roboczego roku podatkowego — limit przychodów dla 9% (art. 19 ust. 1 pkt 2).</summary>
    public static string LimitRateKey(DateOnly taxYearStart) => $"cit.kursEurLimitu9.{taxYearStart:yyyy-MM-dd}";
    /// <summary>Kurs EUR NBP z pierwszego dnia roboczego października roku poprzedniego — status małego podatnika (art. 4a pkt 10).</summary>
    public static string StatusRateKey(int octoberYear) => $"cit.kursEurMalyPodatnik.{octoberYear}";

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public CitService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public async Task<CitAdjustment> AddAdjustmentAsync(Guid fiscalYearId, int periodNumber, CitAdjustmentType type, decimal amount, string description, string? legalBasis, CancellationToken ct = default, Guid? openItemId = null, bool capitalGains = false, bool bookedInPriorYears = false, bool? permanentDifference = null)
    {
        _user.Require(Permission.ManageCit);
        if (amount <= 0) throw new DomainException("Kwota korekty musi być dodatnia (kierunek określa rodzaj korekty).");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("Opisz korektę (udokumentowanie różnicy).");
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        if (periodNumber < 1 || periodNumber > fy.Periods.Count) throw new DomainException("Nieprawidłowy numer okresu.");
        await EnsureManualPeriodEditableAsync(fiscalYearId, periodNumber, ct);
        if (openItemId is { } itemId)
        {
            if (type is not (CitAdjustmentType.KosztNKUP or CitAdjustmentType.DodatkowyKUP))
                throw new DomainException("Korekta zobowiązania art. 18f musi zwiększać albo zmniejszać dochód (NKUP / dodatkowy KUP).");
            await EnsureManualLiabilityAsync(itemId, ct);
        }
        else if (type is CitAdjustmentType.KosztNKUP or CitAdjustmentType.ZmniejszeniePrzychodow && permanentDifference is null)
            throw new DomainException("Wskaż rodzaj różnicy: trwała (np. kary, reprezentacja, przychody zwolnione) albo przejściowa (np. koszt rozliczany podatkowo w innym roku) — decyduje o polach RPD JPK_CIT.");
        else if (IsArticle18f(description, legalBasis))
            throw new DomainException("Powiąż ręczną korektę art. 18f z konkretnym zobowiązaniem.");
        var a = new CitAdjustment
        {
            CompanyId = _user.CompanyId,
            FiscalYearId = fiscalYearId,
            PeriodNumber = periodNumber,
            Type = type,
            Amount = Money.Round(amount),
            Description = description.Trim(),
            LegalBasis = legalBasis,
            OpenItemId = openItemId,
            CapitalGains = capitalGains,
            BookedInPriorYears = bookedInPriorYears && type is CitAdjustmentType.ZwiekszeniePrzychodow or CitAdjustmentType.DodatkowyKUP,
            PermanentDifference = openItemId is not null ? false : type is CitAdjustmentType.KosztNKUP or CitAdjustmentType.ZmniejszeniePrzychodow ? permanentDifference : null,
            UserId = _user.UserId,
            CreatedAtUtc = _clock.UtcNow
        };
        _db.CitAdjustments.Add(a);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_KOREKTA", "CitAdjustment", a.Id.ToString(), $"{type} {Money.Xml(amount)}: {description}", ct);
        await _db.SaveChangesAsync(ct);
        return a;
    }

    /// <summary>Kurs EUR NBP z pierwszego dnia roboczego roku podatkowego (limit przychodów bieżącego roku dla stawki 9%).</summary>
    public Task<decimal?> LimitEurRateAsync(DateOnly taxYearStart, CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync<decimal?>(_user.CompanyId, LimitRateKey(taxYearStart), () => null, ct);

    public Task SetLimitEurRateAsync(DateOnly taxYearStart, decimal rate, CancellationToken ct = default) =>
        SetRateAsync(LimitRateKey(taxYearStart), rate, ct);

    /// <summary>Kurs EUR NBP z pierwszego dnia roboczego października roku poprzedzającego rok podatkowy (status małego podatnika).</summary>
    public Task<decimal?> StatusEurRateAsync(int octoberYear, CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync<decimal?>(_user.CompanyId, StatusRateKey(octoberYear), () => null, ct);

    /// <summary>1 października poprzedniego roku podatkowego (z danych programu albo — gdy poprzedniego roku nie ma — roku o tej samej długości).</summary>
    public async Task<DateOnly> StatusOctoberAsync(Domain.Periods.FiscalYear fy, CancellationToken ct = default)
    {
        var prev = await _db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == fy.CompanyId && f.EndDate == fy.StartDate.AddDays(-1), ct);
        return CitCalculator.StatusRateOctober(prev?.StartDate ?? fy.StartDate.AddYears(-1), fy.StartDate.AddDays(-1));
    }

    public Task SetStatusEurRateAsync(int octoberYear, decimal rate, CancellationToken ct = default) =>
        SetRateAsync(StatusRateKey(octoberYear), rate, ct);

    private async Task SetRateAsync(string key, decimal rate, CancellationToken ct)
    {
        _user.Require(Permission.ManageCit);
        if (rate is <= 0 or > 20) throw new DomainException("Podaj średni kurs euro z tabeli A NBP (np. 4,2156).");
        await new SettingsService(_db, _clock).SetAsync<decimal?>(_user.CompanyId, key, Math.Round(rate, 4), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_KURS_EUR", "AppSetting", key, Money.Xml(rate), ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Wyłączenie stawki 9% (art. 19 ust. 1b–1c): podmiot powstały z restrukturyzacji itp.</summary>
    public Task<bool> ReducedRateExcludedAsync(CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync(_user.CompanyId, ExclusionKey, () => false, ct);

    public Task<bool> JpkCit2025Async(CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync(_user.CompanyId, JpkCit2025Key, () => false, ct);

    public async Task SetJpkCit2025Async(bool value, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, JpkCit2025Key, value, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetReducedRateExcludedAsync(bool excluded, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, ExclusionKey, excluded, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_WYLACZENIE_9", "AppSetting", ExclusionKey, excluded.ToString(), ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<CitReconciliation> ReconcileAsync(Guid fiscalYearId, int uptoPeriod, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        var period = fy.Periods.Single(p => p.Number == uptoPeriod);
        var company = await _db.Companies.FirstAsync(c => c.Id == fy.CompanyId, ct);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fy.Id, fy.StartDate, period.EndDate, true, ct);
        var r = new CitReconciliation { From = fy.StartDate, To = period.EndDate };

        // amortyzacja: księgowa vs podatkowa
        var charges = await _db.DepreciationCharges.AsNoTracking()
            .Where(c => c.CompanyId == fy.CompanyId && c.JournalEntryId != null)
            .ToListAsync(ct);
        var inRange = charges.Where(c => new DateOnly(c.Year, c.Month, 1) >= fy.StartDate && new DateOnly(c.Year, c.Month, 1) <= period.EndDate).ToList();
        var bookDep = inRange.Sum(c => c.BookAmount);
        var taxDep = inRange.Sum(c => c.TaxAmount - c.TaxNonDeductibleAmount);
        var depAccounts = (await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId).Select(a => a.ExpenseAccountNumber).Distinct().ToListAsync(ct)).ToHashSet();

        decimal taxRev = 0, taxCost = 0, capRev = 0, capCost = 0;
        foreach (var t in tb.Where(t => t.IsLeaf && t.Account.IsResultAccount))
        {
            var a = t.Account;
            var value = a.Type == AccountType.WynikowePrzychodowe ? -(t.CumulativeDebit - t.CumulativeCredit) : t.CumulativeDebit - t.CumulativeCredit;
            if (value == 0) continue;
            if (a.Type == AccountType.WynikowePrzychodowe) r.BookRevenue += value; else r.BookCosts += value;
            if (a.Number is "870" or "872") { r.Adjustments.Add(new($"Podatek dochodowy ({a.Number}) — NKUP", value, "art. 16 ust. 1 pkt 15 ustawy o CIT")); continue; }
            if (depAccounts.Contains(a.Number)) continue; // zastępujemy całą amortyzację bilansową podatkową jeden raz
            switch (a.CitClassification)
            {
                case CitClassification.PrzychodPodatkowy: taxRev += value; break;
                case CitClassification.PrzychodZyskiKapitalowe: capRev += value; break;
                case CitClassification.KosztZyskiKapitalowe: capCost += value; break;
                case CitClassification.PrzychodNiepodatkowy:
                    r.Adjustments.Add(new($"Przychody niepodatkowe — {a.Number} {a.Name}", -value, "klasyfikacja konta")); break;
                case CitClassification.KosztPodatkowy: taxCost += value; break;
                case CitClassification.KosztNiepodatkowy:
                    r.Adjustments.Add(new($"Koszty NKUP — {a.Number} {a.Name}", value, "klasyfikacja konta")); break;
                case CitClassification.DoAnalizy:
                case CitClassification.NieDotyczy:
                    if (depAccounts.Contains(a.Number)) break; // amortyzacja — zastępowana podatkową
                    if (a.Type == AccountType.WynikowePrzychodowe) taxRev += value; else taxCost += value;
                    r.ToAnalyze.Add((a.Number, a.Name, value));
                    break;
            }
        }
        if (depAccounts.Count > 0)
        {
            var bookOnDepAccounts = tb.Where(t => t.IsLeaf && depAccounts.Contains(t.Account.Number)).Sum(t => t.CumulativeDebit - t.CumulativeCredit);
            taxCost += taxDep;
            if (bookOnDepAccounts != bookDep)
                r.Warnings.Add($"Saldo kont amortyzacji ({Money.Format(bookOnDepAccounts)}) ≠ suma naliczeń modułu ŚT ({Money.Format(bookDep)}) — sprawdź ręczne zapisy amortyzacji.");
            if (taxDep != bookOnDepAccounts) r.Adjustments.Add(new("Różnica amortyzacji bilansowej i podatkowej", bookOnDepAccounts - taxDep, "art. 16a–16m ustawy o CIT"));
        }
        // pozycje z klasyfikacją CIT nadpisującą konto (np. część NKUP kosztów samochodu osobowego, reprezentacja)
        var overrides = await (from l in _db.JournalLines.AsNoTracking()
                               join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                               where e.FiscalYearId == fy.Id && e.Status == EntryStatus.Zaksiegowany && e.Kind != EntryKind.Zamkniecie
                                     && e.PostingDate <= period.EndDate && l.CitClassification != null
                               select new { l.AccountId, l.Debit, l.Credit, l.CitClassification }).ToListAsync(ct);
        var accById = tb.ToDictionary(t => t.Account.Id, t => t.Account);
        var nkupLines = overrides.Where(o => o.CitClassification == CitClassification.KosztNiepodatkowy && accById.TryGetValue(o.AccountId, out var a)
                                             && a.Type == AccountType.WynikoweKosztowe && a.CitClassification != CitClassification.KosztNiepodatkowy
                                             && !depAccounts.Contains(a.Number)).ToList();
        var nkupTotal = nkupLines.Sum(o => o.Debit - o.Credit);
        if (nkupTotal != 0)
        {
            var capitalNkup = nkupLines.Where(o => accById[o.AccountId].CitClassification == CitClassification.KosztZyskiKapitalowe).Sum(o => o.Debit - o.Credit);
            taxCost -= nkupTotal - capitalNkup;
            capCost -= capitalNkup;
            r.Adjustments.Add(new("Koszty NKUP — części wydatków wskazane w dokumentach (kategorie wydatków)", nkupTotal, "art. 16 ust. 1 ustawy o CIT (np. pkt 28, 51)"));
            foreach (var g in nkupLines.GroupBy(o => o.AccountId))
            {
                var idx = r.ToAnalyze.FindIndex(x => x.Account == accById[g.Key].Number);
                if (idx >= 0) r.ToAnalyze[idx] = (r.ToAnalyze[idx].Account, r.ToAnalyze[idx].Name, r.ToAnalyze[idx].Amount - g.Sum(o => o.Debit - o.Credit));
            }
        }
        r.BookResultBeforeTax = r.BookRevenue - (r.BookCosts - tb.Where(t => t.IsLeaf && t.Account.Number is "870" or "872").Sum(t => t.CumulativeDebit - t.CumulativeCredit));

        // RPD: różnice trwałe i przejściowe. Przejściowe z wyceny — zapisy z kontami odpisów aktualizujących i rezerw:
        // utworzenie (NKUP) → K_5, wykorzystanie/rozwiązanie zmniejszające NKUP → K_6, przychód z rozwiązania → K_2.
        var valuationAccounts = new[] { Settlements.ValuationService.AllowanceAccount, Settlements.ValuationService.ProvisionAccount, Settlements.ValuationService.EmployeeProvisionAccount };
        var valuationLines = await (from l in _db.JournalLines.AsNoTracking()
                                    join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                                    where e.FiscalYearId == fy.Id && e.Status == EntryStatus.Zaksiegowany && e.Kind != EntryKind.Zamkniecie && e.Kind != EntryKind.BilansOtwarcia
                                          && e.PostingDate <= period.EndDate
                                          && _db.JournalLines.Any(v => v.EntryId == e.Id && valuationAccounts.Contains(v.AccountNumber))
                                    select new { l.EntryId, l.AccountId, l.Debit, l.Credit, l.CitClassification }).ToListAsync(ct);
        decimal valNkupUp = 0, valNkupDown = 0, valRevenue = 0;
        foreach (var g in valuationLines.Where(l => accById.TryGetValue(l.AccountId, out var a) && a.IsResultAccount && !depAccounts.Contains(a.Number)).GroupBy(l => l.EntryId))
        {
            var nkup = g.Where(l => (l.CitClassification ?? accById[l.AccountId].CitClassification) == CitClassification.KosztNiepodatkowy && accById[l.AccountId].Type == AccountType.WynikoweKosztowe)
                .Sum(l => l.Debit - l.Credit);
            if (nkup > 0) valNkupUp += nkup; else valNkupDown -= nkup;
            valRevenue += g.Where(l => accById[l.AccountId].CitClassification == CitClassification.PrzychodNiepodatkowy && accById[l.AccountId].Type == AccountType.WynikowePrzychodowe)
                .Sum(l => l.Credit - l.Debit);
        }
        var permanentRevenue = r.Adjustments.Where(a => a.Item.StartsWith("Przychody niepodatkowe")).Sum(a => -a.Amount) - valRevenue;
        var allNkup = r.Adjustments.Where(a => a.Item.StartsWith("Koszty NKUP")).Sum(a => a.Amount);
        var depNonDeductible = inRange.Sum(c => c.TaxNonDeductibleAmount);
        var depTemporary = depAccounts.Count == 0 ? 0m : tb.Where(t => t.IsLeaf && depAccounts.Contains(t.Account.Number)).Sum(t => t.CumulativeDebit - t.CumulativeCredit) - inRange.Sum(c => c.TaxAmount);
        // wykorzystanie/rozwiązanie najpierw dotyczy odpisów i rezerw z bilansu otwarcia (NKUP lat ubiegłych → K_6),
        // nadwyżka — rezerw utworzonych w tym roku (pomniejsza K_5)
        var priorValuation = Math.Max(0, tb.Where(t => t.IsLeaf && valuationAccounts.Any(v => t.Account.Number == v || t.Account.Number.StartsWith(v + "-")))
            .Sum(t => t.OpeningCredit - t.OpeningDebit));
        var valPrior = Math.Min(valNkupDown, priorValuation);
        var valCurrent = valNkupUp - (valNkupDown - valPrior);
        decimal k1 = permanentRevenue, k2 = valRevenue, k3 = 0, k4 = allNkup - (valNkupUp - valNkupDown) + depNonDeductible,
                k5 = Math.Max(0, valCurrent) + Math.Max(0, depTemporary), k6 = valPrior + Math.Max(0, -valCurrent), k7 = 0, k8 = Math.Max(0, -depTemporary);

        var liability = await UnpaidLiabilitiesAsync(fy, period.EndDate, r, ct);
        var increasedThisYear = liability.Where(a => a.Kind == CitLiabilityAdjustmentKind.Zwiekszenie).Select(a => a.OpenItemId).ToHashSet();
        foreach (var adj in liability)
        {
            var sign = adj.Kind == CitLiabilityAdjustmentKind.Zwiekszenie ? 1 : -1;
            taxCost -= sign * adj.AmountOperating;
            capCost -= sign * adj.AmountCapital;
            var amount = adj.AmountOperating + adj.AmountCapital;
            if (sign > 0) k5 += amount;                                     // KUP ujęty w księgach, niezapłacony — przejściowo nie KUP
            else if (increasedThisYear.Contains(adj.OpenItemId)) k5 -= amount; // zapłata w tym samym roku znosi zwiększenie
            else k6 += amount;                                              // zwiększenie z lat ubiegłych — KUP w roku zapłaty
        }

        var adjustments = await _db.CitAdjustments.AsNoTracking().Where(a => a.FiscalYearId == fy.Id && a.PeriodNumber <= uptoPeriod).ToListAsync(ct);
        foreach (var a in adjustments)
        {
            switch (a.Type)
            {
                case CitAdjustmentType.ZwiekszeniePrzychodow: taxRev += a.Amount; if (a.BookedInPriorYears) k3 += a.Amount; else k7 += a.Amount; break;
                case CitAdjustmentType.ZmniejszeniePrzychodow:
                    taxRev -= a.Amount;
                    if (a.PermanentDifference == true) k1 += a.Amount; else k2 += a.Amount;
                    break;
                case CitAdjustmentType.KosztNKUP:
                    if (a.CapitalGains) capCost -= a.Amount; else taxCost -= a.Amount;
                    if (a.PermanentDifference == true) k4 += a.Amount; else k5 += a.Amount;
                    break;
                case CitAdjustmentType.DodatkowyKUP:
                    if (a.CapitalGains) capCost += a.Amount; else taxCost += a.Amount;
                    if (a.BookedInPriorYears || a.OpenItemId is not null) k6 += a.Amount; else k8 += a.Amount;
                    break;
                case CitAdjustmentType.OdliczenieOdDochodu: r.Deductions += a.Amount; break;
            }
            r.Adjustments.Add(new($"Korekta: {a.Description}", a.Type is CitAdjustmentType.ZmniejszeniePrzychodow or CitAdjustmentType.DodatkowyKUP or CitAdjustmentType.OdliczenieOdDochodu ? -a.Amount : a.Amount, a.LegalBasis ?? "korekta ręczna"));
        }
        // straty z lat ubiegłych (art. 7 ust. 5) — odrębnie dla źródeł; odliczenia także w zaliczkach od wskazanego okresu
        var lossDeductions = await (from d in _db.TaxLossDeductions.AsNoTracking()
                                    join l in _db.TaxLosses.AsNoTracking() on d.TaxLossId equals l.Id
                                    where d.FiscalYearId == fy.Id && d.PeriodNumber <= uptoPeriod
                                    select new { d.Amount, l.CapitalGains, l.YearLabel }).ToListAsync(ct);
        var opDeductions = r.Deductions + lossDeductions.Where(x => !x.CapitalGains).Sum(x => x.Amount);
        var capDeductions = lossDeductions.Where(x => x.CapitalGains).Sum(x => x.Amount);
        foreach (var x in lossDeductions)
            r.Adjustments.Add(new($"Odliczenie straty z roku {x.YearLabel}{(x.CapitalGains ? " (zyski kapitałowe)" : "")}", -x.Amount, "art. 7 ust. 5 ustawy o CIT"));
        r.Deductions = opDeductions + capDeductions;

        // limit kosztów finansowania dłużnego (art. 15c): koszty finansowania wg kwalifikacji konta (art. 15c ust. 12 — odsetki,
        // prowizje, opłaty, premie, część odsetkowa leasingu, różnice kursowe od finansowania); bez kwalifikacji — konta z pozycją
        // RZiS „Odsetki” (R.H.I) będące KUP; przychody odsetkowe — R.G.II. Limit: wyższa z kwot 3 mln zł (rok ≠ 12 mies.: 250 000 zł
        // za każdy rozpoczęty miesiąc roku podatkowego) albo 30% podatkowej EBITDA (przychody ze wszystkich źródeł bez odsetkowych
        // minus KUP bez amortyzacji i kosztów finansowania). Nadwyżka nie jest KUP — korekta automatyczna (różnica przejściowa,
        // rozliczenie w 5 kolejnych latach, art. 15c ust. 18–19; nadwyżka — definicja z ust. 3).
        bool FinancingCost(Account a) => a.DebtFinancing switch
        {
            DebtFinancingKind.Koszt => true,
            null => a.ProfitLossLine == "R.H.I" && a.CitClassification != CitClassification.KosztNiepodatkowy && !depAccounts.Contains(a.Number),
            _ => false
        };
        bool FinancingRevenue(Account a) => a.DebtFinancing switch
        {
            DebtFinancingKind.PrzychodOdsetkowy => true,
            null => a.ProfitLossLine == "R.G.II" && a.CitClassification != CitClassification.PrzychodNiepodatkowy,
            _ => false
        };
        var interestCosts = tb.Where(t => t.IsLeaf && t.Account.Type == AccountType.WynikoweKosztowe && FinancingCost(t.Account)).Sum(t => t.CumulativeDebit - t.CumulativeCredit);
        var interestIncome = tb.Where(t => t.IsLeaf && t.Account.Type == AccountType.WynikowePrzychodowe && FinancingRevenue(t.Account)).Sum(t => t.CumulativeCredit - t.CumulativeDebit);
        var netFinancing = interestCosts - interestIncome;
        var amountLimit = CitCalculator.DebtFinancingAmountLimit(fy.StartDate, fy.EndDate);
        var financingEbitda = (taxRev + capRev - interestIncome) - (taxCost + capCost - interestCosts - taxDep);
        var financingLimit = Math.Max(amountLimit, Money.Round(0.3m * financingEbitda));
        r.DebtFinancingHeadroom = Math.Max(0m, financingLimit - Math.Max(0m, netFinancing));
        if (netFinancing > amountLimit)
            r.Warnings.Add($"Art. 15c: koszty finansowania netto {Money.Format(netFinancing)} zł przekraczają limit kwotowy — wyliczenie opiera się na oznaczeniach kont i wymaga weryfikacji (art. 15c ust. 12: koszty skapitalizowane w wartości aktywów rozliczane w odpisach amortyzacyjnych, wyłączenia ustawowe, finansowanie od podmiotów szczególnych, np. fundacji rodzinnej, część odsetkowa leasingu, różnice kursowe). Nie jest to wynik zweryfikowany automatycznie.");
        if (netFinancing > amountLimit)
        {
            var ebitda = financingEbitda;
            var limit = financingLimit;
            if (netFinancing > limit)
            {
                var excess = Money.Round(netFinancing - limit);
                // podział nadwyżki między źródła proporcjonalnie do kosztów finansowania zaliczonych do każdego z nich
                var capitalInterest = tb.Where(t => t.IsLeaf && t.Account.Type == AccountType.WynikoweKosztowe && FinancingCost(t.Account)
                                                    && t.Account.CitClassification == CitClassification.KosztZyskiKapitalowe).Sum(t => t.CumulativeDebit - t.CumulativeCredit);
                var excessCapital = interestCosts == 0 ? 0m : Money.Round(excess * capitalInterest / interestCosts);
                var excessOperating = excess - excessCapital;
                taxCost -= excessOperating;
                capCost -= excessCapital;
                k5 += excess;
                r.DebtFinancingExcessOperating = excessOperating;
                r.DebtFinancingExcessCapital = excessCapital;
                if (excessOperating != 0)
                    r.Adjustments.Add(new($"Nadwyżka kosztów finansowania dłużnego ponad limit {Money.Format(limit)} zł", excessOperating, "art. 15c ust. 1 i 1a ustawy o CIT"));
                if (excessCapital != 0)
                    r.Adjustments.Add(new($"Nadwyżka kosztów finansowania dłużnego ponad limit {Money.Format(limit)} zł — zyski kapitałowe", excessCapital, "art. 15c ust. 1 i 1a ustawy o CIT"));
                r.Warnings.Add($"Art. 15c: wyłączono z KUP nadwyżkę kosztów finansowania dłużnego {Money.Format(excess)} zł (limit kwotowy {Money.Format(amountLimit)} zł, 30% EBITDA {Money.Format(Money.Round(0.3m * ebitda))} zł). Nadwyżkę zalicza się do KUP w 5 kolejnych latach w ramach limitu (korekta „dodatkowy KUP” z oznaczeniem lat ubiegłych). Koszty wyłączone z limitu oznacz na koncie (Plan kont → art. 15c: nie dotyczy).");
            }
        }

        // art. 15c ust. 18: koszty wyłączone w latach ubiegłych zaliczone do KUP w tym roku (rozliczenie roczne) — w granicach wolnego limitu;
        // koszty ujęte w księgach lat ubiegłych → RPD K_6
        if (uptoPeriod == fy.Periods.Count)
        {
            var uses = await (from u in _db.DebtFinancingCarryforwardUses.AsNoTracking()
                              join c in _db.DebtFinancingCarryforwards.AsNoTracking() on u.CarryforwardId equals c.Id
                              where u.FiscalYearId == fy.Id
                              orderby c.YearEnd
                              select new { u.Amount, c.CapitalGains, c.YearLabel }).ToListAsync(ct);
            var room = r.DebtFinancingHeadroom;
            foreach (var u in uses)
            {
                var applied = Math.Min(u.Amount, room);
                room -= applied;
                if (applied < u.Amount)
                    r.Warnings.Add($"Art. 15c ust. 18: zaliczenie nadwyżki z roku {u.YearLabel} ({Money.Format(u.Amount)} zł) przekracza wolny limit roku — uwzględniono {Money.Format(applied)} zł. Popraw rozliczenie nadwyżek.");
                if (applied == 0) continue;
                if (u.CapitalGains) capCost += applied; else taxCost += applied;
                k6 += applied;
                r.Adjustments.Add(new($"Nadwyżka kosztów finansowania dłużnego z roku {u.YearLabel}{(u.CapitalGains ? " (zyski kapitałowe)" : "")} — zaliczona do KUP", -applied, "art. 15c ust. 18 ustawy o CIT"));
            }
        }

        if (adjustments.Any(a => a.Type is CitAdjustmentType.KosztNKUP or CitAdjustmentType.ZmniejszeniePrzychodow && a.PermanentDifference is null && a.OpenItemId is null))
            r.Warnings.Add("Część korekt CIT nie ma określonego rodzaju różnicy (trwała/przejściowa) — w RPD przyjęto różnicę przejściową (K_2/K_5); popraw korektę przed wysłaniem JPK_CIT.");
        r.TaxRevenue = taxRev + capRev;
        r.TaxCosts = taxCost + capCost;
        r.Rpd = new CitRpd(k1, k2, k3, k4, k5, k6, k7, k8);
        var rpdGap = r.Rpd.TaxIncomeFrom(r.BookResultBeforeTax) - (r.TaxRevenue - r.TaxCosts);
        if (rpdGap != 0)
            r.Warnings.Add($"RPD nie uzgadnia się z dochodem o {Money.Format(rpdGap)} zł (pozycje „do analizy” lub ręczne zapisy amortyzacji) — sprawdź przed wysłaniem JPK_CIT.");
        var settings = new SettingsService(_db, _clock);
        var eurRate = await settings.GetAsync<decimal?>(fy.CompanyId, LimitRateKey(fy.StartDate), () => null, ct);
        var excluded = await settings.GetAsync(fy.CompanyId, ExclusionKey, () => false, ct);
        var small = company.IsSmallCitTaxpayer;
        if (small && !fy.IsFirstYear && !excluded)
        {
            var check = await SmallTaxpayerCheckAsync(fy, settings, ct);
            r.Warnings.Add(check.Message);
            small = !check.Exceeded;
            r.SmallTaxpayerEvidenceMissing = !check.Verified;
        }
        // limit dotyczy wszystkich przychodów osiągniętych w roku podatkowym (obu źródeł)
        r.Rate = CitCalculator.DecideRate(small || fy.IsFirstYear, excluded, eurRate,
            CitCalculator.FullMonths(fy.StartDate, fy.EndDate), taxRev + capRev);
        r.Operating = CitCalculator.Compute(taxRev, taxCost, opDeductions, r.Rate.RatePercent);
        r.CapitalGains = CitCalculator.Compute(capRev, capCost, capDeductions, CitCalculator.StandardRate);
        if (opDeductions > r.Operating.Income || capDeductions > r.CapitalGains.Income)
            r.Warnings.Add("Odliczenia przekraczają dochód ze źródła — nadwyżka nie zmniejsza podatku (pomniejsz kwotę odliczenia straty w tym roku).");
        r.Computation = new CitComputation(r.Operating.TaxRevenue + r.CapitalGains.TaxRevenue, r.Operating.TaxCosts + r.CapitalGains.TaxCosts,
            r.Operating.Income + r.CapitalGains.Income, r.Operating.Loss + r.CapitalGains.Loss, r.Deductions,
            r.Operating.TaxBase + r.CapitalGains.TaxBase, r.Rate.RatePercent, r.Operating.Tax + r.CapitalGains.Tax);
        if (r.ToAnalyze.Count > 0) r.Warnings.Add($"{r.ToAnalyze.Count} kont(a) wymaga analizy podatkowej (np. odsetki, różnice kursowe, wynagrodzenia — art. 15 ust. 4g/4h, art. 16 ust. 1 pkt 57 ustawy o CIT). Przyjęto wartości bilansowe.");
        r.Warnings.Add("Stawka: " + r.Rate.Reason);
        if (r.Rate.RateMissing)
            r.Warnings.Add($"Uzupełnij kurs euro NBP z pierwszego dnia roboczego roku podatkowego ({fy.StartDate.ToPl()} lub następny dzień roboczy) — bez niego wyliczenia nie można zapisać jako zaliczki.");
        if (capRev != 0 || capCost != 0)
            r.Warnings.Add($"Zyski kapitałowe rozliczono odrębnie po 19%: dochód {Money.Format(r.CapitalGains.Income)}, strata {Money.Format(r.CapitalGains.Loss)} (straty nie kompensuje się między źródłami; odliczenia przypisano do innych źródeł).");
        r.Warnings.Add("Wyliczenie nie uwzględnia automatycznie ulg (np. B+R, IP Box, darowizny — wprowadź jako odliczenie) ani limitu art. 15e — wymagana weryfikacja przez doradcę podatkowego.");
        return r;
    }

    /// <summary>
    /// Status małego podatnika (art. 4a pkt 10): przychody ze sprzedaży wraz z VAT w poprzednim roku podatkowym ≤ 2 mln euro
    /// po kursie NBP z pierwszego dnia roboczego października poprzedniego roku. Sprawdzane z faktur sprzedaży, jeśli poprzedni
    /// rok jest prowadzony w programie; w przeciwnym razie status pozostaje deklaracją użytkownika.
    /// </summary>
    private async Task<(bool Exceeded, bool Verified, string Message)> SmallTaxpayerCheckAsync(Domain.Periods.FiscalYear fy, SettingsService settings, CancellationToken ct)
    {
        var confirmedGross = await settings.GetAsync<decimal?>(fy.CompanyId, $"cit.sprzedazPoprzedniegoRoku.{fy.Id}", () => null, ct);
        var october = await StatusOctoberAsync(fy, ct);
        var statusRate = await StatusEurRateAsync(october.Year, ct);
        if (confirmedGross is { } verified && statusRate is { } fx)
        {
            var verifiedLimit = CitCalculator.SmallTaxpayerLimitPln(fx);
            return (verified > verifiedLimit, true, $"Potwierdzona kompletna sprzedaż z VAT: {Money.Format(verified)} zł; limit małego podatnika {Money.Format(verifiedLimit)} zł.");
        }
        var prev = await _db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == fy.CompanyId && f.EndDate == fy.StartDate.AddDays(-1), ct);
        if (prev is null) return (false, false, "Status małego podatnika (art. 4a pkt 10) zadeklarowany w danych spółki — poprzedni rok nie jest prowadzony w programie, potwierdź kompletną ewidencję przychodów ze sprzedaży z VAT ≤ 2 mln euro.");
        var octoberYear = october.Year;
        var rate = await settings.GetAsync<decimal?>(fy.CompanyId, StatusRateKey(octoberYear), () => null, ct);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == fy.CompanyId && (d.Kind == Domain.Documents.DocumentKind.FakturaSprzedazy || d.Kind == Domain.Documents.DocumentKind.KorektaSprzedazy)
                        && d.Status == Domain.Documents.DocumentStatus.Zaksiegowany
                        && d.OperationDate >= prev.StartDate && d.OperationDate <= prev.EndDate)
            .Select(d => d.GrossAmountPln).ToListAsync(ct);
        var gross = docs.Sum();
        if (rate is null)
            return (false, false, $"Status małego podatnika: sprzedaż z VAT w roku {prev.Name} = {Money.Format(gross)} zł — uzupełnij kurs euro z 1. dnia roboczego października {octoberYear} r., aby program sprawdził limit 2 mln euro.");
        var limit = CitCalculator.SmallTaxpayerLimitPln(rate.Value);
        return (gross > limit, false, gross <= limit
            ? $"Sprzedaż z zaksięgowanych dokumentów w roku {prev.Name}: {Money.Format(gross)} zł ≤ {Money.Format(limit)} zł. To kontrola pomocnicza — status wymaga potwierdzenia kompletnej ewidencji sprzedaży z VAT."
            : $"Sprzedaż z VAT w roku {prev.Name}: {Money.Format(gross)} zł > {Money.Format(limit)} zł — zastosowano 19% mimo oznaczenia małego podatnika.");
    }

    public async Task SetPreviousSalesAsync(Guid fiscalYearId, decimal grossSales, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (grossSales < 0) throw new DomainException("Sprzedaż nie może być ujemna.");
        await _db.FiscalYears.FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        await new SettingsService(_db, _clock).SetAsync<decimal?>(_user.CompanyId, $"cit.sprzedazPoprzedniegoRoku.{fiscalYearId}", grossSales, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_SPRZEDAZ_POTWIERDZONA", "FiscalYear", fiscalYearId.ToString(), $"Kompletna ewidencja sprzedaży z VAT: {Money.Xml(grossSales)}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Art. 18f ust. 1 pkt 2 ustawy o CIT (dłużnik): zobowiązanie z transakcji handlowej (kontrahent — przedsiębiorca)
    /// niezapłacone w ciągu 90 dni od upływu terminu płatności zwiększa podstawę opodatkowania w okresie, w którym upłynął
    /// 90. dzień — także przy ustalaniu zaliczek. Zwiększenie obejmuje tylko część zobowiązania zaliczoną do kosztów uzyskania
    /// przychodów (wg dekretu dokumentu: konta kosztowe z klasyfikacją KUP; NKUP i VAT odliczony nie są zwiększane),
    /// osobno dla źródła „inne” i „zyski kapitałowe”. Zapłata zmniejsza podstawę wyłącznie do wysokości wcześniejszych
    /// zwiększeń zapisanych w historii (<see cref="CitLiabilityAdjustment"/>). Zakupy ujęte na kontach aktywów (środki trwałe,
    /// zapasy) wymagają indywidualnej oceny i są tylko sygnalizowane.
    /// </summary>
    private async Task<List<CitLiabilityAdjustment>> UnpaidLiabilitiesAsync(Domain.Periods.FiscalYear fy, DateOnly asOf, CitReconciliation r, CancellationToken ct)
    {
        var result = new List<CitLiabilityAdjustment>();
        var items = await (from o in _db.OpenItems.AsNoTracking()
                           join d in _db.Documents.AsNoTracking() on o.DocumentId equals d.Id
                           where o.CompanyId == fy.CompanyId && o.Side == Domain.Settlements.OpenItemSide.Zobowiazanie && o.DueDate != null
                                 && (d.Kind == Domain.Documents.DocumentKind.FakturaZakupu || d.Kind == Domain.Documents.DocumentKind.Rachunek)
                           select new { o.Id, o.Reference, o.AmountPln, o.JournalEntryId, DueDate = o.DueDate!.Value }).ToListAsync(ct);
        items = items.Where(i => i.DueDate.AddDays(90) <= asOf && i.AmountPln > 0).ToList();
        if (items.Count == 0) return result;
        var ids = items.Select(i => i.Id).ToList();
        var allocations = await _db.SettlementAllocations.AsNoTracking().Where(a => ids.Contains(a.OpenItemId) && !a.IsReversed && a.Date <= asOf)
            .Select(a => new { a.OpenItemId, a.Date, a.AmountPln }).ToListAsync(ct);
        var history = await _db.CitLiabilityAdjustments.AsNoTracking().Where(h => ids.Contains(h.OpenItemId) && h.FiscalYearId != fy.Id && h.Date < fy.StartDate).ToListAsync(ct);
        var manualItems = await _db.CitAdjustments.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId && a.OpenItemId != null)
            .Select(a => a.OpenItemId!.Value).Distinct().ToListAsync(ct);
        var entryIds = items.Select(i => i.JournalEntryId).Distinct().ToList();
        var lines = await (from l in _db.JournalLines.AsNoTracking()
                           join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                           where entryIds.Contains(l.EntryId)
                           select new { l.EntryId, l.Debit, l.Credit, l.CitClassification, AccountClass = a.CitClassification, a.Type, a.Number }).ToListAsync(ct);
        var assetPurchases = new List<string>();
        foreach (var i in items)
        {
            if (manualItems.Contains(i.Id))
            {
                r.Warnings.Add($"Art. 18f: {i.Reference} rozliczane ręcznie — automatyczna korekta wyłączona, aby uniknąć podwójnego ujęcia.");
                continue;
            }
            decimal op = 0, cap = 0, assets = 0;
            foreach (var l in lines.Where(l => l.EntryId == i.JournalEntryId && l.Debit != 0))
            {
                if (l.Type == AccountType.WynikoweKosztowe)
                {
                    var cls = l.CitClassification ?? l.AccountClass;
                    if (cls == CitClassification.KosztZyskiKapitalowe) cap += l.Debit;
                    else if (cls == CitClassification.KosztPodatkowy) op += l.Debit;
                    else if (cls is CitClassification.DoAnalizy or CitClassification.NieDotyczy)
                        r.Warnings.Add($"Art. 18f: {i.Reference} wymaga analizy części podatkowej — nierozstrzygniętej kwoty nie objęto automatyczną korektą; wprowadź udokumentowaną korektę ręczną.");
                }
                else if (!l.Number.StartsWith("22")) assets += l.Debit; // VAT naliczony (22x) nie jest kosztem
            }
            if (assets > 0) assetPurchases.Add(i.Reference);
            if (op + cap == 0) continue;
            var qualification = await new SettingsService(_db, _clock).GetAsync<bool?>(fy.CompanyId, $"cit.art18f.{i.Id}", () => null, ct);
            if (qualification != true)
            {
                if (qualification is null)
                    r.Warnings.Add($"Art. 18f: {i.Reference} wymaga analizy warunków transakcji i wyłączeń — brak kwalifikacji; korektę po weryfikacji wprowadź ręcznie.");
                continue;
            }
            var ratioOp = op / i.AmountPln;
            var ratioCap = cap / i.AmountPln;
            var day90 = i.DueDate.AddDays(90);
            var paid = allocations.Where(a => a.OpenItemId == i.Id).ToList();
            if (day90 >= fy.StartDate)
            {
                var unpaidAt90 = i.AmountPln - paid.Where(a => a.Date <= day90).Sum(a => a.AmountPln);
                if (unpaidAt90 <= 0) continue;
                result.Add(Adj(fy, i.Id, i.Reference, CitLiabilityAdjustmentKind.Zwiekszenie, day90, Money.Round(unpaidAt90 * ratioOp), Money.Round(unpaidAt90 * ratioCap)));
                var paidAfter = Math.Min(unpaidAt90, paid.Where(a => a.Date > day90).Sum(a => a.AmountPln));
                if (paidAfter > 0)
                    result.Add(Adj(fy, i.Id, i.Reference, CitLiabilityAdjustmentKind.Zmniejszenie, paid.Where(a => a.Date > day90).Max(a => a.Date),
                        Money.Round(paidAfter * ratioOp), Money.Round(paidAfter * ratioCap)));
            }
            else
            {
                var paidThisYear = paid.Where(a => a.Date >= fy.StartDate).Sum(a => a.AmountPln);
                if (paidThisYear <= 0) continue;
                var own = history.Where(h => h.OpenItemId == i.Id).ToList();
                decimal Net(Func<CitLiabilityAdjustment, decimal> f) => own.Sum(h => h.Kind == CitLiabilityAdjustmentKind.Zwiekszenie ? f(h) : -f(h));
                var netOp = Net(h => h.AmountOperating);
                var netCap = Net(h => h.AmountCapital);
                if (netOp <= 0 && netCap <= 0)
                {
                    r.Warnings.Add($"Zapłacono w tym roku zobowiązanie {i.Reference} przeterminowane ponad 90 dni w latach poprzednich, ale w programie brak zapisu zwiększenia dochodu (art. 18f) — zmniejszenie wprowadź ręcznie, jeśli zwiększenie zostało dokonane.");
                    continue;
                }
                result.Add(Adj(fy, i.Id, i.Reference, CitLiabilityAdjustmentKind.Zmniejszenie, paid.Where(a => a.Date >= fy.StartDate).Max(a => a.Date),
                    Math.Min(Math.Max(0, netOp), Money.Round(paidThisYear * ratioOp)), Math.Min(Math.Max(0, netCap), Money.Round(paidThisYear * ratioCap))));
            }
        }
        foreach (var (kind, item, basis) in new[]
                 {
                     (CitLiabilityAdjustmentKind.Zwiekszenie, "Niezapłacone zobowiązania — 90 dni po terminie płatności (zwiększenie podstawy, część zaliczona do KUP)", "art. 18f ust. 1 pkt 2 ustawy o CIT; uwzględniane w zaliczkach (art. 25 ust. 1)"),
                     (CitLiabilityAdjustmentKind.Zmniejszenie, "Zapłata zobowiązań, o które wcześniej zwiększono podstawę (zmniejszenie)", "art. 18f ustawy o CIT — do wysokości wcześniejszego zwiększenia")
                 })
        {
            var sign = kind == CitLiabilityAdjustmentKind.Zwiekszenie ? 1 : -1;
            var op = result.Where(x => x.Kind == kind).Sum(x => x.AmountOperating);
            var cap = result.Where(x => x.Kind == kind).Sum(x => x.AmountCapital);
            if (op != 0) r.Adjustments.Add(new(item, sign * op, basis));
            if (cap != 0) r.Adjustments.Add(new(item + " — zyski kapitałowe", sign * cap, basis));
        }
        if (assetPurchases.Count > 0)
            r.Warnings.Add($"Zobowiązania przeterminowane ponad 90 dni z zakupów ujętych na kontach aktywów ({string.Join(", ", assetPurchases.Take(5))}) — art. 18f dla środków trwałych i zapasów wymaga indywidualnej oceny (część zaliczona do KUP przez amortyzację lub zużycie).");
        r.LiabilityAdjustments.AddRange(result);
        return result;
    }

    /// <summary>Udokumentowana kwalifikacja transakcji i wyłączeń art. 18f; NIP sam nie potwierdza spełnienia warunków.</summary>
    public async Task QualifyLiabilityAsync(Guid openItemId, bool applicable, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Podaj uzasadnienie kwalifikacji art. 18f (warunki i wyłączenia).");
        await _db.OpenItems.FirstAsync(o => o.Id == openItemId && o.CompanyId == _user.CompanyId, ct);
        var currentQualification = await new SettingsService(_db, _clock).GetAsync<bool?>(_user.CompanyId, $"cit.art18f.{openItemId}", () => null, ct);
        if (currentQualification != applicable && await (from h in _db.CitLiabilityAdjustments
            join a in _db.CitAdvances on h.FiscalYearId equals a.FiscalYearId
            where h.OpenItemId == openItemId && a.IsApproved && a.PeriodEnd >= h.Date
            select a.Id).AnyAsync(ct))
            throw new DomainException("Kwalifikacja wpływa na zatwierdzone rozliczenie art. 18f — nie można jej zmienić.");
        if (applicable)
        {
            if (await _db.CitAdjustments.AnyAsync(a => a.CompanyId == _user.CompanyId && a.OpenItemId == openItemId, ct))
                throw new DomainException("Zobowiązanie ma ręczne korekty art. 18f. Usuń je kontrolowaną operacją przed przejściem na automat; zatwierdzonych rozliczeń nie można zmienić.");
            var unlinked = await _db.CitAdjustments.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId && a.OpenItemId == null).ToListAsync(ct);
            if (unlinked.Any(a => IsArticle18f(a.Description, a.LegalBasis)))
                throw new DomainException("Istnieją stare ręczne korekty art. 18f bez powiązania — najpierw przypisz im zobowiązania.");
        }
        await new SettingsService(_db, _clock).SetAsync<bool?>(_user.CompanyId, $"cit.art18f.{openItemId}", applicable, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_ART18F_KWALIFIKACJA", "OpenItem", openItemId.ToString(), $"{applicable}: {justification}", ct);
        await _db.SaveChangesAsync(ct);
    }

    private static bool IsArticle18f(string description, string? basis) =>
        System.Text.RegularExpressions.Regex.IsMatch(description + " " + basis, @"18\s*f", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task EnsureManualPeriodEditableAsync(Guid yearId, int period, CancellationToken ct)
    {
        if (await _db.CitAdvances.AnyAsync(a => a.FiscalYearId == yearId && a.PeriodNumber >= period && a.IsApproved, ct))
            throw new DomainException("Korekta wpływa na zatwierdzoną zaliczkę — rozliczenie jest zablokowane.");
    }

    private async Task EnsureManualLiabilityAsync(Guid itemId, CancellationToken ct)
    {
        await _db.OpenItems.FirstAsync(o => o.Id == itemId && o.CompanyId == _user.CompanyId && o.Side == Domain.Settlements.OpenItemSide.Zobowiazanie, ct);
        if (await new SettingsService(_db, _clock).GetAsync<bool?>(_user.CompanyId, $"cit.art18f.{itemId}", () => null, ct) == true)
            throw new DomainException("Zobowiązanie jest rozliczane automatycznie — najpierw wyłącz kwalifikację art. 18f.");
    }

    public async Task LinkManualLiabilityAsync(Guid adjustmentId, Guid itemId, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        var adjustment = await _db.CitAdjustments.FirstAsync(a => a.Id == adjustmentId && a.CompanyId == _user.CompanyId, ct);
        await EnsureManualPeriodEditableAsync(adjustment.FiscalYearId, adjustment.PeriodNumber, ct);
        await EnsureManualLiabilityAsync(itemId, ct);
        if (adjustment.Type is not (CitAdjustmentType.KosztNKUP or CitAdjustmentType.DodatkowyKUP))
            throw new DomainException("To nie jest korekta kosztów art. 18f.");
        adjustment.OpenItemId = itemId;
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_KOREKTA_POWIAZANA", "CitAdjustment", adjustmentId.ToString(), itemId.ToString(), ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveManualAdjustmentAsync(Guid adjustmentId, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        var adjustment = await _db.CitAdjustments.FirstAsync(a => a.Id == adjustmentId && a.CompanyId == _user.CompanyId, ct);
        await EnsureManualPeriodEditableAsync(adjustment.FiscalYearId, adjustment.PeriodNumber, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_KOREKTA_USUNIETA", "CitAdjustment", adjustmentId.ToString(),
            System.Text.Json.JsonSerializer.Serialize(adjustment), ct);
        _db.CitAdjustments.Remove(adjustment);
        await _db.SaveChangesAsync(ct);
    }

    private CitLiabilityAdjustment Adj(Domain.Periods.FiscalYear fy, Guid itemId, string reference, CitLiabilityAdjustmentKind kind, DateOnly date, decimal op, decimal cap) => new()
    {
        CompanyId = fy.CompanyId, FiscalYearId = fy.Id, OpenItemId = itemId, Reference = reference, Kind = kind, Date = date,
        AmountOperating = op, AmountCapital = cap, RecordedAtUtc = _clock.UtcNow
    };

    // ---- straty podatkowe (art. 7 ust. 5) ----

    public async Task<TaxLoss> AddLossAsync(string yearLabel, DateOnly yearEnd, bool capitalGains, decimal amount, string? note, CancellationToken ct = default,
        DateOnly? deductibleUntil = null, string? deductibleUntilBasis = null, Guid? deductibleUntilEvidenceId = null)
    {
        _user.Require(Permission.ManageCit);
        if (deductibleUntil is { } du) TaxLossRules.ValidateConfirmedEnd(yearEnd, du, deductibleUntilBasis, await TaxYearsAsync(ct));
        await EnsureEvidenceAsync(deductibleUntilEvidenceId, ct);
        if (amount <= 0) throw new DomainException("Kwota straty musi być dodatnia.");
        if (string.IsNullOrWhiteSpace(yearLabel)) throw new DomainException("Podaj rok podatkowy straty.");
        if (await _db.TaxLosses.AnyAsync(l => l.CompanyId == _user.CompanyId && l.YearEnd == yearEnd && l.CapitalGains == capitalGains, ct))
            throw new DomainException("Strata z tego roku i źródła jest już zarejestrowana.");
        var loss = new TaxLoss
        {
            CompanyId = _user.CompanyId, YearLabel = yearLabel.Trim(), YearEnd = yearEnd, CapitalGains = capitalGains, Amount = Money.Round(amount),
            Note = note, DeductibleUntil = deductibleUntil, DeductibleUntilBasis = deductibleUntil is null ? null : deductibleUntilBasis!.Trim(),
            DeductibleUntilEvidenceId = deductibleUntil is null ? null : deductibleUntilEvidenceId, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        };
        _db.TaxLosses.Add(loss);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_STRATA_ZAREJESTROWANA", "TaxLoss", loss.Id.ToString(),
            $"{yearLabel} {(capitalGains ? "zyski kapitałowe" : "inne źródła")}: {Money.Xml(amount)}{(deductibleUntil is { } u ? $"; odliczenie do {u.ToPl()} — potwierdzone przez operatora na podstawie: {deductibleUntilBasis!.Trim()}" : "")}", ct);
        await _db.SaveChangesAsync(ct);
        return loss;
    }

    /// <summary>Rejestracja straty z rocznego wyliczenia zamkniętego roku (oba źródła).</summary>
    public async Task<List<TaxLoss>> RegisterLossesFromYearAsync(Guid fiscalYearId, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        if (fy.Status is not (Domain.Periods.FiscalYearStatus.Zamkniety or Domain.Periods.FiscalYearStatus.Zarchiwizowany))
            throw new DomainException($"Rok {fy.Name} nie jest zamknięty — stratę rejestruje się po zamknięciu ksiąg i weryfikacji rozliczenia rocznego (CIT-8).");
        var rec = await ReconcileAsync(fy.Id, fy.Periods.Count, ct);
        if (rec.ToAnalyze.Count > 0)
            throw new DomainException("Wyliczenie roczne zawiera pozycje „do analizy podatkowej” — zweryfikuj je przed rejestracją straty.");
        var result = new List<TaxLoss>();
        if (rec.Operating.Loss > 0) result.Add(await AddLossAsync(fy.Name, fy.EndDate, false, rec.Operating.Loss, "z rocznego wyliczenia CIT", ct));
        if (rec.CapitalGains.Loss > 0) result.Add(await AddLossAsync(fy.Name, fy.EndDate, true, rec.CapitalGains.Loss, "z rocznego wyliczenia CIT", ct));
        if (result.Count == 0) throw new DomainException("W tym roku nie wystąpiła strata podatkowa.");
        return result;
    }

    public async Task<TaxLossDeduction> DeductLossAsync(Guid lossId, Guid fiscalYearId, int fromPeriod, decimal amount, bool oneTime, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        var loss = await _db.TaxLosses.FirstAsync(l => l.Id == lossId && l.CompanyId == _user.CompanyId, ct);
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        if (fromPeriod < 1 || fromPeriod > fy.Periods.Count) throw new DomainException("Nieprawidłowy okres.");
        await EnsureManualPeriodEditableAsync(fiscalYearId, fromPeriod, ct);
        var previous = await _db.TaxLossDeductions.Where(d => d.TaxLossId == lossId).ToListAsync(ct);
        if (previous.Any(d => d.FiscalYearId == fiscalYearId)) throw new DomainException("Odliczenie tej straty w tym roku jest już wprowadzone.");
        var years = await _db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == _user.CompanyId).Select(f => new { f.StartDate, f.EndDate }).ToListAsync(ct);
        if (fy.StartDate <= loss.YearEnd) throw new DomainException("Stratę odlicza się w latach następujących po roku jej poniesienia.");
        TaxLossRules.Validate(loss, previous, TaxLossRules.TaxYearNumber(loss.YearEnd, years.Select(y => (y.StartDate, y.EndDate)).ToList(), fy.StartDate), fy.EndDate, Money.Round(amount), oneTime);
        var d = new TaxLossDeduction
        {
            CompanyId = _user.CompanyId, TaxLossId = lossId, FiscalYearId = fiscalYearId, PeriodNumber = fromPeriod, Amount = Money.Round(amount), OneTime = oneTime,
            UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        };
        _db.TaxLossDeductions.Add(d);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_STRATA_ODLICZONA", "TaxLossDeduction", d.Id.ToString(), $"strata {loss.YearLabel}: {Money.Xml(amount)} w roku {fy.Name}{(oneTime ? " (jednorazowo)" : "")}", ct);
        await _db.SaveChangesAsync(ct);
        return d;
    }

    /// <summary>Rejestracja nadwyżki kosztów finansowania dłużnego z lat sprzed prowadzenia ksiąg w programie (z CIT-8 / ewidencji).</summary>
    public async Task<DebtFinancingCarryforward> AddDebtFinancingCarryforwardAsync(string yearLabel, DateOnly yearEnd, bool capitalGains, decimal amount, string? note,
        DateOnly? deductibleUntil = null, CancellationToken ct = default, string? deductibleUntilBasis = null, Guid? deductibleUntilEvidenceId = null)
    {
        _user.Require(Permission.ManageCit);
        if (amount <= 0) throw new DomainException("Kwota nadwyżki musi być dodatnia.");
        if (string.IsNullOrWhiteSpace(yearLabel)) throw new DomainException("Podaj rok podatkowy, w którym powstała nadwyżka.");
        if (string.IsNullOrWhiteSpace(note))
            throw new DomainException("Wskaż źródło kwoty (np. CIT-8 za dany rok, wyliczenie limitu art. 15c) — kwota musi wynikać z dokumentacji podatnika.");
        if (deductibleUntil is { } du) TaxLossRules.ValidateConfirmedEnd(yearEnd, du, deductibleUntilBasis, await TaxYearsAsync(ct));
        await EnsureEvidenceAsync(deductibleUntilEvidenceId, ct);
        if (await _db.DebtFinancingCarryforwards.AnyAsync(c => c.CompanyId == _user.CompanyId && c.YearEnd == yearEnd && c.CapitalGains == capitalGains, ct))
            throw new DomainException("Nadwyżka z tego roku i źródła jest już zarejestrowana.");
        var c = new DebtFinancingCarryforward
        {
            CompanyId = _user.CompanyId, YearLabel = yearLabel.Trim(), YearEnd = yearEnd, CapitalGains = capitalGains, Amount = Money.Round(amount),
            Note = note, DeductibleUntil = deductibleUntil, DeductibleUntilBasis = deductibleUntil is null ? null : deductibleUntilBasis!.Trim(),
            DeductibleUntilEvidenceId = deductibleUntil is null ? null : deductibleUntilEvidenceId, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        };
        _db.DebtFinancingCarryforwards.Add(c);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_15C_NADWYZKA", "DebtFinancingCarryforward", c.Id.ToString(),
            $"{yearLabel} {(capitalGains ? "zyski kapitałowe" : "inne źródła")}: {Money.Xml(amount)}", ct);
        await _db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>Rejestracja nadwyżki z rocznego wyliczenia zamkniętego roku (odrębnie dla źródeł).</summary>
    public async Task<List<DebtFinancingCarryforward>> RegisterDebtFinancingExcessFromYearAsync(Guid fiscalYearId, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        if (fy.Status is not (Domain.Periods.FiscalYearStatus.Zamkniety or Domain.Periods.FiscalYearStatus.Zarchiwizowany))
            throw new DomainException($"Rok {fy.Name} nie jest zamknięty — nadwyżkę rejestruje się po zamknięciu ksiąg i weryfikacji rozliczenia rocznego (CIT-8).");
        var rec = await ReconcileAsync(fy.Id, fy.Periods.Count, ct);
        if (rec.ToAnalyze.Count > 0)
            throw new DomainException("Wyliczenie roczne zawiera pozycje „do analizy podatkowej” — zweryfikuj je przed rejestracją nadwyżki.");
        var result = new List<DebtFinancingCarryforward>();
        if (rec.DebtFinancingExcessOperating > 0)
            result.Add(await AddDebtFinancingCarryforwardAsync(fy.Name, fy.EndDate, false, rec.DebtFinancingExcessOperating, "z rocznego wyliczenia CIT", ct: ct));
        if (rec.DebtFinancingExcessCapital > 0)
            result.Add(await AddDebtFinancingCarryforwardAsync(fy.Name, fy.EndDate, true, rec.DebtFinancingExcessCapital, "z rocznego wyliczenia CIT", ct: ct));
        if (result.Count == 0) throw new DomainException("W tym roku nie wystąpiła nadwyżka kosztów finansowania dłużnego.");
        return result;
    }

    /// <summary>
    /// Zaliczenie wyłączonych kosztów z lat ubiegłych do KUP roku <paramref name="fiscalYearId"/> (art. 15c ust. 18): tylko w 5
    /// kolejnych latach podatkowych po roku ich wyłączenia i tylko w granicach wolnego limitu tego roku (limit − koszty finansowania
    /// netto roku). Wymaga oświadczenia o braku wyłączeń z art. 15c ust. 19 (koszty nie pochodzą od podmiotu przekształconego,
    /// łączonego lub dzielonego, w którego prawa podatnik wstąpił; podatnik nie rozlicza ich jako PGK utworzona po roku wyłączenia).
    /// </summary>
    public async Task<DebtFinancingCarryforwardUse> UseDebtFinancingCarryforwardAsync(Guid carryforwardId, Guid fiscalYearId, decimal amount, string exceptionsStatement, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        amount = Money.Round(amount);
        if (amount <= 0) throw new DomainException("Kwota musi być dodatnia.");
        if (string.IsNullOrWhiteSpace(exceptionsStatement))
            throw new DomainException("Potwierdź brak wyłączeń z art. 15c ust. 19 ustawy o CIT (sukcesja przy przekształceniu, łączeniu lub podziale; podatkowa grupa kapitałowa) i wskaż podstawę.");
        var c = await _db.DebtFinancingCarryforwards.FirstAsync(x => x.Id == carryforwardId && x.CompanyId == _user.CompanyId, ct);
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        await EnsureManualPeriodEditableAsync(fiscalYearId, fy.Periods.Count, ct);
        if (fy.StartDate <= c.YearEnd) throw new DomainException("Nadwyżkę zalicza się do kosztów w latach następujących po roku jej powstania.");
        var years = await TaxYearsAsync(ct);
        var number = TaxLossRules.TaxYearNumber(c.YearEnd, years, fy.StartDate);
        if (number is null && c.DeductibleUntil is null)
            throw new DomainException($"Lat podatkowych między rokiem {c.YearLabel} a {fy.Name} nie ma w programie — wpisz koniec okresu rozliczenia nadwyżki (ostatni dzień 5. roku podatkowego po jej powstaniu).");
        if (number > 5 || c.DeductibleUntil is { } until && fy.EndDate > until)
            throw new DomainException($"Nadwyżkę z roku {c.YearLabel} można zaliczyć do kosztów tylko w 5 kolejnych latach podatkowych (art. 15c ust. 18).");
        var used = await _db.DebtFinancingCarryforwardUses.Where(u => u.CarryforwardId == c.Id).ToListAsync(ct);
        if (used.Any(u => u.FiscalYearId == fiscalYearId)) throw new DomainException("Zaliczenie tej nadwyżki w tym roku jest już wprowadzone.");
        if (amount > c.Amount - used.Sum(u => u.Amount)) throw new DomainException($"Do rozliczenia pozostało {Money.Format(c.Amount - used.Sum(u => u.Amount))} zł nadwyżki z roku {c.YearLabel}.");
        var rec = await ReconcileAsync(fiscalYearId, fy.Periods.Count, ct);
        var usedThisYear = (await _db.DebtFinancingCarryforwardUses.Where(u => u.FiscalYearId == fiscalYearId && u.CompanyId == _user.CompanyId).ToListAsync(ct)).Sum(u => u.Amount);
        if (usedThisYear + amount > rec.DebtFinancingHeadroom)
            throw new DomainException($"Wolny limit kosztów finansowania dłużnego w roku {fy.Name}: {Money.Format(Math.Max(0, rec.DebtFinancingHeadroom - usedThisYear))} zł (art. 15c ust. 18 — zaliczenie tylko w ramach limitu).");
        var use = new DebtFinancingCarryforwardUse
        {
            CompanyId = _user.CompanyId, CarryforwardId = c.Id, FiscalYearId = fiscalYearId, Amount = amount, ExceptionsStatement = exceptionsStatement.Trim(),
            UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        };
        _db.DebtFinancingCarryforwardUses.Add(use);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_15C_ZALICZENIE", "DebtFinancingCarryforwardUse", use.Id.ToString(),
            $"koszty wyłączone w {c.YearLabel}: {Money.Xml(amount)} w roku {fy.Name} (art. 15c ust. 18); brak wyłączeń ust. 19: {exceptionsStatement.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
        return use;
    }

    /// <summary>
    /// Zarachowanie wpłaty wprowadzone przez operatora na podstawie danych urzędowych (e-Urząd Skarbowy, postanowienie o zaliczeniu
    /// wpłaty — art. 62 Ordynacji podatkowej) — powiązane z konkretną zaksięgowaną wpłatą (pozycja Wn 220-01 w korespondencji
    /// ze środkami pieniężnymi). Ewidencja jest wspólna dla wszystkich lat: jednej wpłaty nie można zarachować ponad jej kwotę,
    /// także na zobowiązania różnych lat. Program nie pobiera danych urzędowych — jest to oświadczenie operatora (opcjonalnie
    /// z dokumentem). Zapisów nie usuwa się: korekta anuluje zapis z przyczyną (historia zachowana).
    /// </summary>
    public sealed record CitPaymentAllocation(DateOnly PaymentDate, decimal Amount, int? AdvancePeriod, bool AnnualTax, string Source, DateOnly RecordedOn, string RecordedBy,
        Guid Id = default, Guid? EvidenceId = null, bool Cancelled = false, string? CancelReason = null, DateOnly? CancelledOn = null, string? CancelledBy = null,
        string Year = "", Guid PaymentLineId = default);
    public const string PaymentAllocationsKey = "cit.wplaty.zarachowanie";

    /// <summary>Zaksięgowana wpłata podatku (pozycja Wn 220-01 w zapisie ze środkami pieniężnymi) i kwota już zarachowana.</summary>
    public sealed record CitPayment(Guid LineId, DateOnly Date, string Document, decimal Amount, decimal Allocated)
    {
        public decimal Unallocated => Amount - Allocated;
    }

    /// <summary>Wszystkie zapisy zarachowania jednostki (wszystkie lata, także anulowane — historia).</summary>
    public async Task<List<CitPaymentAllocation>> PaymentAllocationHistoryAsync(CancellationToken ct = default) =>
        await new SettingsService(_db, _clock).GetAsync(_user.CompanyId, PaymentAllocationsKey, () => new List<CitPaymentAllocation>(), ct);

    /// <summary>Obowiązujące zapisy zarachowania na zobowiązania danego roku podatkowego.</summary>
    public async Task<List<CitPaymentAllocation>> PaymentAllocationsAsync(string year, CancellationToken ct = default) =>
        (await PaymentAllocationHistoryAsync(ct)).Where(a => !a.Cancelled && a.Year == year).ToList();

    /// <summary>Zaksięgowane wpłaty CIT (wszystkie lata) z kwotą zarachowaną.</summary>
    public async Task<List<CitPayment>> CitPaymentsAsync(CancellationToken ct = default)
    {
        var lines = await (from l in _db.JournalLines.AsNoTracking()
                           join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                           where e.CompanyId == _user.CompanyId && e.Status == EntryStatus.Zaksiegowany && e.Kind != EntryKind.BilansOtwarcia
                                 && l.AccountNumber == "220-01" && l.Debit != 0
                                 && _db.JournalLines.Any(o => o.EntryId == e.Id && o.Credit != 0 && (o.AccountNumber.StartsWith("10") || o.AccountNumber.StartsWith("13")))
                           select new { l.Id, e.OperationDate, e.DocumentNumber, l.Debit }).ToListAsync(ct);
        var allocated = (await PaymentAllocationHistoryAsync(ct)).Where(a => !a.Cancelled).GroupBy(a => a.PaymentLineId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        return lines.OrderBy(l => l.OperationDate).Select(l => new CitPayment(l.Id, l.OperationDate, l.DocumentNumber, l.Debit, allocated.GetValueOrDefault(l.Id))).ToList();
    }

    public async Task<CitPaymentAllocation> RecordPaymentAllocationAsync(Guid fiscalYearId, Guid paymentLineId, decimal amount, int? advancePeriod, bool annualTax, string source,
        CancellationToken ct = default, Guid? evidenceId = null)
    {
        _user.Require(Permission.ManageCit);
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        amount = Money.Round(amount);
        if (amount <= 0) throw new DomainException("Kwota zarachowania musi być dodatnia.");
        if (string.IsNullOrWhiteSpace(source)) throw new DomainException("Wskaż źródło potwierdzenia (np. e-Urząd Skarbowy — karta rozliczeń, postanowienie o zaliczeniu wpłaty).");
        if (advancePeriod is { } n && (n < 1 || n > fy.Periods.Count)) throw new DomainException("Nieprawidłowy okres zaliczki.");
        if (advancePeriod is not null && annualTax) throw new DomainException("Wpłatę zarachowuje się na zaliczkę albo na podatek roczny.");
        await EnsureEvidenceAsync(evidenceId, ct);
        // wpłata musi być zaksięgowaną zapłatą podatku (Wn 220-01 ze środkami pieniężnymi); łączne zarachowania (wszystkie lata) ≤ kwota wpłaty
        var payment = (await CitPaymentsAsync(ct)).FirstOrDefault(p => p.LineId == paymentLineId)
                      ?? throw new DomainException("Wskazana pozycja nie jest zaksięgowaną wpłatą podatku (Wn 220-01 w korespondencji z rachunkiem lub kasą).");
        if (amount > payment.Unallocated)
            throw new DomainException($"Wpłata z {payment.Date.ToPl()} ({payment.Document}) na {Money.Format(payment.Amount)} zł ma niezarachowane {Money.Format(payment.Unallocated)} zł — nie można zarachować {Money.Format(amount)} zł (podwójne zarachowanie).");
        var history = await PaymentAllocationHistoryAsync(ct);
        var record = new CitPaymentAllocation(payment.Date, amount, advancePeriod, annualTax, source.Trim(), _clock.Today, _user.Login, Guid.CreateVersion7(), evidenceId,
            Year: fy.Name, PaymentLineId: paymentLineId);
        history.Add(record);
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, PaymentAllocationsKey, history, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_WPLATA_ZARACHOWANA", "FiscalYear", fy.Id.ToString(),
            $"wpłata {payment.Date.ToPl()} ({payment.Document}): {Money.Xml(amount)} → rok {fy.Name}, {(advancePeriod is { } p ? $"zaliczka za okres {p}" : annualTax ? "podatek roczny" : "inne zobowiązanie")}; oświadczenie operatora, źródło: {source.Trim()}", ct);
        await SaveAllocationsAsync(ct);
        return record;
    }

    /// <summary>Korekta: anulowanie zapisu zarachowania z podaniem przyczyny (zapis pozostaje w historii, kwota wpłaty wraca do puli).</summary>
    public async Task CancelPaymentAllocationAsync(Guid allocationId, string reason, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Podaj przyczynę korekty zarachowania.");
        var history = await PaymentAllocationHistoryAsync(ct);
        var i = history.FindIndex(a => a.Id == allocationId && !a.Cancelled);
        if (i < 0) throw new DomainException("Nie znaleziono obowiązującego zapisu zarachowania.");
        history[i] = history[i] with { Cancelled = true, CancelReason = reason.Trim(), CancelledOn = _clock.Today, CancelledBy = _user.Login };
        await new SettingsService(_db, _clock).SetAsync(_user.CompanyId, PaymentAllocationsKey, history, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_WPLATA_ZARACHOWANIE_KOREKTA", "AppSetting", PaymentAllocationsKey,
            $"anulowano {history[i].PaymentDate.ToPl()} {Money.Xml(history[i].Amount)} (rok {history[i].Year}): {reason.Trim()}", ct);
        await SaveAllocationsAsync(ct);
    }

    /// <summary>
    /// Zapis rejestru zarachowań z kontrolą współbieżności: jeżeli inny operator zmienił rejestr po jego odczycie (np. zarachował tę
    /// samą wpłatę), zapis jest odrzucany — bez utraty cudzego zapisu i bez podwójnego zarachowania.
    /// </summary>
    private async Task SaveAllocationsAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // DbUpdateConcurrencyException (zmieniony rejestr) albo równoległe utworzenie rejestru (unikalny klucz)
        {
            foreach (var entry in _db.ChangeTracker.Entries().ToList()) entry.State = EntityState.Detached;
            throw new DomainException("Rejestr zarachowań wpłat CIT został w międzyczasie zmieniony przez innego operatora — zapis odrzucono. Odśwież dane i sprawdź niezarachowaną kwotę wpłaty.");
        }
    }

    private async Task EnsureEvidenceAsync(Guid? attachmentId, CancellationToken ct)
    {
        if (attachmentId is { } id && !await _db.Attachments.AnyAsync(a => a.Id == id && a.CompanyId == _user.CompanyId, ct))
            throw new DomainException("Nie znaleziono dokumentu potwierdzającego w magazynie załączników.");
    }

    private async Task<List<(DateOnly Start, DateOnly End)>> TaxYearsAsync(CancellationToken ct) =>
        (await _db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == _user.CompanyId).Select(f => new { f.StartDate, f.EndDate }).ToListAsync(ct))
        .Select(y => (y.StartDate, y.EndDate)).OrderBy(y => y.StartDate).ToList();

    /// <summary>Rejestr nadwyżek: kwota zaliczona i koniec okresu rozliczenia (null — lat nie ma w programie, do potwierdzenia).</summary>
    public async Task<List<(DebtFinancingCarryforward Item, decimal Used, DateOnly? DeductibleUntil)>> DebtFinancingCarryforwardsAsync(CancellationToken ct = default)
    {
        var items = await _db.DebtFinancingCarryforwards.AsNoTracking().Where(c => c.CompanyId == _user.CompanyId).OrderBy(c => c.YearEnd).ThenBy(c => c.CapitalGains).ToListAsync(ct);
        var used = (await _db.DebtFinancingCarryforwardUses.AsNoTracking().Where(u => u.CompanyId == _user.CompanyId).ToListAsync(ct)).GroupBy(u => u.CarryforwardId).ToDictionary(g => g.Key, g => g.Sum(u => u.Amount));
        var years = (await _db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == _user.CompanyId).Select(f => new { f.StartDate, f.EndDate }).ToListAsync(ct))
            .Select(y => (y.StartDate, y.EndDate)).ToList();
        return items.Select(i =>
        {
            var chain = TaxLossRules.Chain(i.YearEnd, years);
            DateOnly? until = i.DeductibleUntil ?? (chain.Count >= 5 ? chain[4].End : null);
            return (i, used.GetValueOrDefault(i.Id), until);
        }).ToList();
    }

    /// <summary>Straty z kwotą odliczoną i końcem okresu odliczenia (wspólny mechanizm <see cref="TaxLossRules.WindowEnd"/>; null — do potwierdzenia).</summary>
    public async Task<List<(TaxLoss Loss, decimal Used, DateOnly? DeductibleUntil, bool Projected)>> LossesAsync(CancellationToken ct = default)
    {
        var losses = await _db.TaxLosses.AsNoTracking().Where(l => l.CompanyId == _user.CompanyId).OrderBy(l => l.YearEnd).ToListAsync(ct);
        var used = (await _db.TaxLossDeductions.AsNoTracking().Where(d => d.CompanyId == _user.CompanyId).ToListAsync(ct)).GroupBy(d => d.TaxLossId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        var years = (await _db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == _user.CompanyId).Select(f => new { f.StartDate, f.EndDate }).ToListAsync(ct))
            .Select(y => (y.StartDate, y.EndDate)).OrderBy(y => y.StartDate).ToList();
        return losses.Select(l =>
        {
            var (end, projected) = TaxLossRules.Window(l, years);
            return (l, used.GetValueOrDefault(l.Id), end, projected);
        }).ToList();
    }

    /// <summary>Potwierdzenie końca okresu odliczenia straty (gdy lat podatkowych po stracie nie ma w programie).</summary>
    public async Task ConfirmLossWindowAsync(Guid lossId, DateOnly deductibleUntil, string basis, CancellationToken ct = default, Guid? evidenceId = null)
    {
        _user.Require(Permission.ManageCit);
        var loss = await _db.TaxLosses.FirstAsync(l => l.Id == lossId && l.CompanyId == _user.CompanyId, ct);
        TaxLossRules.ValidateConfirmedEnd(loss.YearEnd, deductibleUntil, basis, await TaxYearsAsync(ct));
        await EnsureEvidenceAsync(evidenceId, ct);
        loss.DeductibleUntil = deductibleUntil;
        loss.DeductibleUntilBasis = basis.Trim();
        loss.DeductibleUntilEvidenceId = evidenceId;
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_STRATA_OKRES", "TaxLoss", loss.Id.ToString(),
            $"{loss.YearLabel}: odliczenie do {deductibleUntil.ToPl()} — potwierdzone przez operatora na podstawie: {basis.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Jednostki transparentne podatkowo (spółka jawna, partnerska, cywilna, osoba fizyczna) nie są podatnikami CIT.</summary>
    public static void EnsureCitTaxpayer(Company company)
    {
        if (!LegalForms.IsCitTaxpayer(company.LegalForm))
            throw new DomainException($"Jednostka ({LegalForms.Name(company.LegalForm)}) nie jest podatnikiem CIT — podatek dochodowy od jej dochodu rozliczają wspólnicy/właściciel (PIT). " +
                "Program nie wylicza zaliczek PIT; uzgodnienie wyniku bilansowego z podatkowym (raport) pozostaje dostępne pomocniczo.");
    }

    /// <summary>Wyliczenie i zapis zaliczki za okres (narastająco od początku roku).</summary>
    public async Task<CitAdvance> ComputeAdvanceAsync(Guid fiscalYearId, int periodNumber, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        var later = await _db.CitAdvances.Where(a => a.FiscalYearId == fiscalYearId && a.PeriodNumber > periodNumber)
            .OrderBy(a => a.PeriodNumber).ToListAsync(ct);
        if (later.Any(a => a.IsApproved))
            throw new DomainException("Późniejsza zaliczka jest zatwierdzona — wymagane kontrolowane ponowne otwarcie rozliczeń.");
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var advance = await ComputeSingleAdvanceAsync(fiscalYearId, periodNumber, ct);
        foreach (var next in later) await ComputeSingleAdvanceAsync(fiscalYearId, next.PeriodNumber, ct);
        await transaction.CommitAsync(ct);
        return advance;
    }

    private async Task<CitAdvance> ComputeSingleAdvanceAsync(Guid fiscalYearId, int periodNumber, CancellationToken ct)
    {
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId && f.CompanyId == _user.CompanyId, ct);
        var company = await _db.Companies.FirstAsync(c => c.Id == fy.CompanyId, ct);
        EnsureCitTaxpayer(company);
        if (company.CitAdvanceMethod == CitAdvanceMethod.Kwartalne && periodNumber % 3 != 0)
            throw new DomainException("Przy zaliczkach kwartalnych wylicza się je za okres kończący kwartał (3, 6, 9, 12).");
        var rec = await ReconcileAsync(fiscalYearId, periodNumber, ct);
        if (rec.SmallTaxpayerEvidenceMissing && rec.Rate.RatePercent == CitCalculator.ReducedRate)
            throw new DomainException("Potwierdź kompletną sprzedaż z VAT poprzedniego roku i kurs październikowy w warunkach stawki 9%.");
        if (rec.Rate.RateMissing)
            throw new DomainException($"Uzupełnij kurs euro NBP z pierwszego dnia roboczego roku podatkowego (limit przychodów dla stawki 9%, art. 19 ust. 1 pkt 2) — sekcja CIT → „Warunki stawki 9%” (można pobrać z NBP).");
        var approved = await _db.CitAdvances.AnyAsync(a => a.FiscalYearId == fiscalYearId && a.PeriodNumber == periodNumber && a.IsApproved, ct);
        if (approved) throw new DomainException("Zaliczka za ten okres została zatwierdzona — nie można jej przeliczyć.");
        // Odtwarzamy narastającą historię do najpóźniejszego zapisanego okresu, również przy przeliczeniu wcześniejszej zaliczki.
        var lastPeriod = await _db.CitAdvances.Where(a => a.FiscalYearId == fiscalYearId)
            .Select(a => (int?)a.PeriodNumber).MaxAsync(ct) ?? periodNumber;
        var historyRec = lastPeriod > periodNumber ? await ReconcileAsync(fiscalYearId, lastPeriod, ct) : rec;
        var savedHistory = await _db.CitLiabilityAdjustments.Where(h => h.FiscalYearId == fiscalYearId).ToListAsync(ct);
        foreach (var calculated in historyRec.LiabilityAdjustments)
        {
            var saved = savedHistory.FirstOrDefault(h => h.OpenItemId == calculated.OpenItemId && h.Kind == calculated.Kind && h.Date == calculated.Date);
            if (saved is null) _db.CitLiabilityAdjustments.Add(calculated);
            else
            {
                saved.AmountOperating = calculated.AmountOperating;
                saved.AmountCapital = calculated.AmountCapital;
                savedHistory.Remove(saved);
            }
        }
        _db.CitLiabilityAdjustments.RemoveRange(savedHistory);
        var previous = await _db.CitAdvances.Where(a => a.FiscalYearId == fiscalYearId && a.PeriodNumber < periodNumber).ToListAsync(ct);
        var prevDue = previous.Sum(a => a.AdvanceDue);
        var existing = await _db.CitAdvances.FirstOrDefaultAsync(a => a.FiscalYearId == fiscalYearId && a.PeriodNumber == periodNumber, ct);
        if (existing?.IsApproved == true) throw new DomainException("Zaliczka za ten okres została zatwierdzona — nie można jej przeliczyć.");
        var period = fy.Periods.Single(p => p.Number == periodNumber);
        var adv = existing ?? new CitAdvance { CompanyId = fy.CompanyId, FiscalYearId = fy.Id, PeriodNumber = periodNumber };
        adv.PeriodEnd = period.EndDate;
        adv.CumulativeTaxRevenue = rec.TaxRevenue;
        adv.CumulativeTaxCosts = rec.TaxCosts;
        adv.CumulativeIncome = rec.Computation.Income;
        adv.Deductions = rec.Deductions;
        adv.TaxBase = rec.Computation.TaxBase;
        adv.RatePercent = rec.Computation.RatePercent;
        adv.CumulativeTax = rec.Computation.Tax;
        adv.PreviouslyDue = prevDue;
        adv.AdvanceDue = CitCalculator.AdvanceDue(rec.Computation.Tax, prevDue);
        adv.PaymentDeadline = CitCalculator.AdvanceDeadline(period.EndDate);
        adv.UserId = _user.UserId;
        adv.CalculatedAtUtc = _clock.UtcNow;
        adv.WarningsText = string.Join(Environment.NewLine, rec.Warnings);
        if (existing is null) _db.CitAdvances.Add(adv);
        // Niezmienny ślad każdego wyliczenia, powiązany z zaliczką i jej okresem, również po kolejnych przeliczeniach.
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_ART18F_STAN_OKRESU", "CitAdvance", adv.Id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(new { fiscalYearId, periodNumber, rec.LiabilityAdjustments }), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_ZALICZKA_WYLICZONA", "CitAdvance", adv.Id.ToString(), $"okres {periodNumber}: podatek narastająco {adv.CumulativeTax}, zaliczka {adv.AdvanceDue}", ct);
        await _db.SaveChangesAsync(ct);
        return adv;
    }

    /// <summary>Zatwierdzenie zaliczki — wymaga potwierdzenia, że pozycje „do analizy” zostały zweryfikowane.</summary>
    public async Task ApproveAdvanceAsync(Guid advanceId, bool analysisConfirmed, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        var adv = await _db.CitAdvances.FirstAsync(a => a.Id == advanceId && a.CompanyId == _user.CompanyId, ct);
        var current = await ReconcileAsync(adv.FiscalYearId, adv.PeriodNumber, ct);
        if (current.Rate.RateMissing || (current.SmallTaxpayerEvidenceMissing && current.Rate.RatePercent == CitCalculator.ReducedRate)
            || current.Computation.Tax != adv.CumulativeTax || current.Computation.RatePercent != adv.RatePercent
            || current.TaxRevenue != adv.CumulativeTaxRevenue || current.TaxCosts != adv.CumulativeTaxCosts
            || current.Deductions != adv.Deductions || current.Computation.TaxBase != adv.TaxBase)
            throw new DomainException("Warunki lub dane CIT zmieniły się albo są niekompletne — przelicz zaliczkę przed zatwierdzeniem.");
        if (!analysisConfirmed && !string.IsNullOrWhiteSpace(adv.WarningsText) && adv.WarningsText.Contains("wymaga analizy"))
            throw new DomainException("Potwierdź weryfikację pozycji wymagających analizy podatkowej przed zatwierdzeniem zaliczki.");
        adv.IsApproved = true;
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_ZALICZKA_ZATWIERDZONA", "CitAdvance", adv.Id.ToString(), $"okres {adv.PeriodNumber}: {adv.AdvanceDue}", ct);
        await _db.SaveChangesAsync(ct);
    }
}
