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
    /// <summary>Korekty z art. 18f wyliczone dla okresu (zapisywane w historii przy zapisie zaliczki).</summary>
    public List<CitLiabilityAdjustment> LiabilityAdjustments { get; } = new();
    public List<string> Warnings { get; } = new();
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

    public async Task<CitAdjustment> AddAdjustmentAsync(Guid fiscalYearId, int periodNumber, CitAdjustmentType type, decimal amount, string description, string? legalBasis, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (amount <= 0) throw new DomainException("Kwota korekty musi być dodatnia (kierunek określa rodzaj korekty).");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("Opisz korektę (udokumentowanie różnicy).");
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
        if (periodNumber < 1 || periodNumber > fy.Periods.Count) throw new DomainException("Nieprawidłowy numer okresu.");
        var a = new CitAdjustment
        {
            CompanyId = _user.CompanyId,
            FiscalYearId = fiscalYearId,
            PeriodNumber = periodNumber,
            Type = type,
            Amount = Money.Round(amount),
            Description = description.Trim(),
            LegalBasis = legalBasis,
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
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
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
            if (a.Number is "870" or "872") { r.Adjustments.Add(new($"Podatek dochodowy ({a.Number}) — NKUP", value, "art. 16 ust. 1 pkt 46 ustawy o CIT")); continue; }
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
            taxCost -= nkupTotal;
            r.Adjustments.Add(new("Koszty NKUP — części wydatków wskazane w dokumentach (kategorie wydatków)", nkupTotal, "art. 16 ust. 1 ustawy o CIT (np. pkt 28, 51)"));
            foreach (var g in nkupLines.GroupBy(o => o.AccountId))
            {
                var idx = r.ToAnalyze.FindIndex(x => x.Account == accById[g.Key].Number);
                if (idx >= 0) r.ToAnalyze[idx] = (r.ToAnalyze[idx].Account, r.ToAnalyze[idx].Name, r.ToAnalyze[idx].Amount - g.Sum(o => o.Debit - o.Credit));
            }
        }
        r.BookResultBeforeTax = r.BookRevenue - (r.BookCosts - tb.Where(t => t.IsLeaf && t.Account.Number is "870" or "872").Sum(t => t.CumulativeDebit - t.CumulativeCredit));

        foreach (var adj in await UnpaidLiabilitiesAsync(fy, period.EndDate, r, ct))
        {
            var sign = adj.Kind == CitLiabilityAdjustmentKind.Zwiekszenie ? 1 : -1;
            taxCost -= sign * adj.AmountOperating;
            capCost -= sign * adj.AmountCapital;
        }

        var adjustments = await _db.CitAdjustments.AsNoTracking().Where(a => a.FiscalYearId == fy.Id && a.PeriodNumber <= uptoPeriod).ToListAsync(ct);
        foreach (var a in adjustments)
        {
            switch (a.Type)
            {
                case CitAdjustmentType.ZwiekszeniePrzychodow: taxRev += a.Amount; break;
                case CitAdjustmentType.ZmniejszeniePrzychodow: taxRev -= a.Amount; break;
                case CitAdjustmentType.KosztNKUP: taxCost -= a.Amount; break;
                case CitAdjustmentType.DodatkowyKUP: taxCost += a.Amount; break;
                case CitAdjustmentType.OdliczenieOdDochodu: r.Deductions += a.Amount; break;
            }
            r.Adjustments.Add(new($"Korekta: {a.Description}", a.Type is CitAdjustmentType.ZmniejszeniePrzychodow or CitAdjustmentType.DodatkowyKUP or CitAdjustmentType.OdliczenieOdDochodu ? -a.Amount : a.Amount, a.LegalBasis ?? "korekta ręczna"));
        }
        r.TaxRevenue = taxRev + capRev;
        r.TaxCosts = taxCost + capCost;
        var settings = new SettingsService(_db, _clock);
        var eurRate = await settings.GetAsync<decimal?>(fy.CompanyId, LimitRateKey(fy.StartDate), () => null, ct);
        var excluded = await settings.GetAsync(fy.CompanyId, ExclusionKey, () => false, ct);
        // limit dotyczy wszystkich przychodów osiągniętych w roku podatkowym (obu źródeł)
        r.Rate = CitCalculator.DecideRate(company.IsSmallCitTaxpayer || fy.IsFirstYear, excluded, eurRate,
            CitCalculator.FullMonths(fy.StartDate, fy.EndDate), taxRev + capRev);
        r.Operating = CitCalculator.Compute(taxRev, taxCost, r.Deductions, r.Rate.RatePercent);
        r.CapitalGains = CitCalculator.Compute(capRev, capCost, 0m, CitCalculator.StandardRate);
        r.Computation = new CitComputation(r.Operating.TaxRevenue + r.CapitalGains.TaxRevenue, r.Operating.TaxCosts + r.CapitalGains.TaxCosts,
            r.Operating.Income + r.CapitalGains.Income, r.Operating.Loss + r.CapitalGains.Loss, r.Deductions,
            r.Operating.TaxBase + r.CapitalGains.TaxBase, r.Rate.RatePercent, r.Operating.Tax + r.CapitalGains.Tax);
        if (r.ToAnalyze.Count > 0) r.Warnings.Add($"{r.ToAnalyze.Count} kont(a) wymaga analizy podatkowej (np. odsetki, różnice kursowe, wynagrodzenia — art. 15 ust. 4g/4h, art. 16 ust. 1 pkt 57 ustawy o CIT). Przyjęto wartości bilansowe.");
        r.Warnings.Add("Stawka: " + r.Rate.Reason);
        if (r.Rate.RateMissing)
            r.Warnings.Add($"Uzupełnij kurs euro NBP z pierwszego dnia roboczego roku podatkowego ({fy.StartDate.ToPl()} lub następny dzień roboczy) — bez niego wyliczenia nie można zapisać jako zaliczki.");
        if (company.IsSmallCitTaxpayer && !fy.IsFirstYear && !excluded)
            r.Warnings.Add(await SmallTaxpayerCheckAsync(fy, settings, ct));
        if (capRev != 0 || capCost != 0)
            r.Warnings.Add($"Zyski kapitałowe rozliczono odrębnie po 19%: dochód {Money.Format(r.CapitalGains.Income)}, strata {Money.Format(r.CapitalGains.Loss)} (straty nie kompensuje się między źródłami; odliczenia przypisano do innych źródeł).");
        r.Warnings.Add("Wyliczenie nie uwzględnia automatycznie: limitów kosztów finansowania dłużnego, strat z lat ubiegłych (wprowadź jako korektę), ulg — wymagana weryfikacja przez doradcę podatkowego.");
        return r;
    }

    /// <summary>
    /// Status małego podatnika (art. 4a pkt 10): przychody ze sprzedaży wraz z VAT w poprzednim roku podatkowym ≤ 2 mln euro
    /// po kursie NBP z pierwszego dnia roboczego października poprzedniego roku. Sprawdzane z faktur sprzedaży, jeśli poprzedni
    /// rok jest prowadzony w programie; w przeciwnym razie status pozostaje deklaracją użytkownika.
    /// </summary>
    private async Task<string> SmallTaxpayerCheckAsync(Domain.Periods.FiscalYear fy, SettingsService settings, CancellationToken ct)
    {
        var prev = await _db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == fy.CompanyId && f.EndDate == fy.StartDate.AddDays(-1), ct);
        if (prev is null) return "Status małego podatnika (art. 4a pkt 10) zadeklarowany w danych spółki — poprzedni rok nie jest prowadzony w programie, potwierdź przychody ze sprzedaży z VAT ≤ 2 mln euro.";
        var octoberYear = prev.EndDate.Year;
        var rate = await settings.GetAsync<decimal?>(fy.CompanyId, StatusRateKey(octoberYear), () => null, ct);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == fy.CompanyId && (d.Kind == Domain.Documents.DocumentKind.FakturaSprzedazy || d.Kind == Domain.Documents.DocumentKind.KorektaSprzedazy)
                        && d.IssueDate >= prev.StartDate && d.IssueDate <= prev.EndDate)
            .Select(d => d.GrossAmountPln).ToListAsync(ct);
        var gross = docs.Sum();
        if (rate is null)
            return $"Status małego podatnika: sprzedaż z VAT w roku {prev.Name} = {Money.Format(gross)} zł — uzupełnij kurs euro z 1. dnia roboczego października {octoberYear} r., aby program sprawdził limit 2 mln euro.";
        var limit = CitCalculator.SmallTaxpayerLimitPln(rate.Value);
        return gross <= limit
            ? $"Status małego podatnika potwierdzony: sprzedaż z VAT w roku {prev.Name} {Money.Format(gross)} zł ≤ {Money.Format(limit)} zł (art. 4a pkt 10)."
            : $"UWAGA: sprzedaż z VAT w roku {prev.Name} {Money.Format(gross)} zł > {Money.Format(limit)} zł — spółka nie jest małym podatnikiem (art. 4a pkt 10); zmień oznaczenie w danych spółki.";
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
                           join c in _db.Counterparties.AsNoTracking() on o.CounterpartyId equals c.Id
                           where o.CompanyId == fy.CompanyId && o.Side == Domain.Settlements.OpenItemSide.Zobowiazanie && o.DueDate != null
                                 && (d.Kind == Domain.Documents.DocumentKind.FakturaZakupu || d.Kind == Domain.Documents.DocumentKind.Rachunek)
                                 && ((c.Nip != null && c.Nip != "") || (c.VatEuNumber != null && c.VatEuNumber != ""))
                           select new { o.Id, o.Reference, o.AmountPln, o.JournalEntryId, DueDate = o.DueDate!.Value }).ToListAsync(ct);
        items = items.Where(i => i.DueDate.AddDays(90) <= asOf && i.AmountPln > 0).ToList();
        if (items.Count == 0) return result;
        var ids = items.Select(i => i.Id).ToList();
        var allocations = await _db.SettlementAllocations.AsNoTracking().Where(a => ids.Contains(a.OpenItemId) && !a.IsReversed && a.Date <= asOf)
            .Select(a => new { a.OpenItemId, a.Date, a.AmountPln }).ToListAsync(ct);
        var history = await _db.CitLiabilityAdjustments.AsNoTracking().Where(h => ids.Contains(h.OpenItemId) && h.FiscalYearId != fy.Id && h.Date < fy.StartDate).ToListAsync(ct);
        var entryIds = items.Select(i => i.JournalEntryId).Distinct().ToList();
        var lines = await (from l in _db.JournalLines.AsNoTracking()
                           join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                           where entryIds.Contains(l.EntryId)
                           select new { l.EntryId, l.Debit, l.Credit, l.CitClassification, AccountClass = a.CitClassification, a.Type, a.Number }).ToListAsync(ct);
        var assetPurchases = new List<string>();
        foreach (var i in items)
        {
            decimal op = 0, cap = 0, assets = 0;
            foreach (var l in lines.Where(l => l.EntryId == i.JournalEntryId && l.Debit != 0))
            {
                if (l.Type == AccountType.WynikoweKosztowe)
                {
                    var cls = l.CitClassification ?? l.AccountClass;
                    if (cls == CitClassification.KosztZyskiKapitalowe) cap += l.Debit;
                    else if (cls is CitClassification.KosztPodatkowy or CitClassification.DoAnalizy or CitClassification.NieDotyczy) op += l.Debit;
                }
                else if (!l.Number.StartsWith("22")) assets += l.Debit; // VAT naliczony (22x) nie jest kosztem
            }
            if (assets > 0) assetPurchases.Add(i.Reference);
            if (op + cap == 0) continue;
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

    private CitLiabilityAdjustment Adj(Domain.Periods.FiscalYear fy, Guid itemId, string reference, CitLiabilityAdjustmentKind kind, DateOnly date, decimal op, decimal cap) => new()
    {
        CompanyId = fy.CompanyId, FiscalYearId = fy.Id, OpenItemId = itemId, Reference = reference, Kind = kind, Date = date,
        AmountOperating = op, AmountCapital = cap, RecordedAtUtc = _clock.UtcNow
    };

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
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
        var company = await _db.Companies.FirstAsync(c => c.Id == fy.CompanyId, ct);
        EnsureCitTaxpayer(company);
        if (company.CitAdvanceMethod == CitAdvanceMethod.Kwartalne && periodNumber % 3 != 0)
            throw new DomainException("Przy zaliczkach kwartalnych wylicza się je za okres kończący kwartał (3, 6, 9, 12).");
        var rec = await ReconcileAsync(fiscalYearId, periodNumber, ct);
        if (rec.Rate.RateMissing)
            throw new DomainException($"Uzupełnij kurs euro NBP z pierwszego dnia roboczego roku podatkowego (limit przychodów dla stawki 9%, art. 19 ust. 1 pkt 2) — sekcja CIT → „Warunki stawki 9%” (można pobrać z NBP).");
        // historia korekt z art. 18f: stan narastająco roku zastępuje poprzedni zapis
        _db.CitLiabilityAdjustments.RemoveRange(await _db.CitLiabilityAdjustments.Where(h => h.FiscalYearId == fiscalYearId).ToListAsync(ct));
        _db.CitLiabilityAdjustments.AddRange(rec.LiabilityAdjustments);
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
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_ZALICZKA_WYLICZONA", "CitAdvance", adv.Id.ToString(), $"okres {periodNumber}: podatek narastająco {adv.CumulativeTax}, zaliczka {adv.AdvanceDue}", ct);
        await _db.SaveChangesAsync(ct);
        return adv;
    }

    /// <summary>Zatwierdzenie zaliczki — wymaga potwierdzenia, że pozycje „do analizy” zostały zweryfikowane.</summary>
    public async Task ApproveAdvanceAsync(Guid advanceId, bool analysisConfirmed, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        var adv = await _db.CitAdvances.FirstAsync(a => a.Id == advanceId, ct);
        if (!analysisConfirmed && !string.IsNullOrWhiteSpace(adv.WarningsText) && adv.WarningsText.Contains("wymaga analizy"))
            throw new DomainException("Potwierdź weryfikację pozycji wymagających analizy podatkowej przed zatwierdzeniem zaliczki.");
        adv.IsApproved = true;
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_ZALICZKA_ZATWIERDZONA", "CitAdvance", adv.Id.ToString(), $"okres {adv.PeriodNumber}: {adv.AdvanceDue}", ct);
        await _db.SaveChangesAsync(ct);
    }
}
