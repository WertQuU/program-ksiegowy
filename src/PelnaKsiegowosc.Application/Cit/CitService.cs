using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
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
    public CitComputation Computation { get; set; } = null!;
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

        decimal taxRev = 0, taxCost = 0;
        foreach (var t in tb.Where(t => t.IsLeaf && t.Account.IsResultAccount))
        {
            var a = t.Account;
            var value = a.Type == AccountType.WynikowePrzychodowe ? -(t.CumulativeDebit - t.CumulativeCredit) : t.CumulativeDebit - t.CumulativeCredit;
            if (value == 0) continue;
            if (a.Type == AccountType.WynikowePrzychodowe) r.BookRevenue += value; else r.BookCosts += value;
            if (a.Number == "870") { r.Adjustments.Add(new($"Podatek dochodowy ({a.Number}) — NKUP", value, "art. 16 ust. 1 pkt 46 ustawy o CIT")); continue; }
            switch (a.CitClassification)
            {
                case CitClassification.PrzychodPodatkowy: taxRev += value; break;
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
                                             && a.Type == AccountType.WynikoweKosztowe && a.CitClassification != CitClassification.KosztNiepodatkowy).ToList();
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
        r.BookResultBeforeTax = r.BookRevenue - (r.BookCosts - tb.Where(t => t.IsLeaf && t.Account.Number == "870").Sum(t => t.CumulativeDebit - t.CumulativeCredit));

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
        r.TaxRevenue = taxRev;
        r.TaxCosts = taxCost;
        var rate = CitCalculator.RateFor(company.IsSmallCitTaxpayer || fy.IsFirstYear);
        r.Computation = CitCalculator.Compute(taxRev, taxCost, r.Deductions, rate);
        if (r.ToAnalyze.Count > 0) r.Warnings.Add($"{r.ToAnalyze.Count} kont(a) wymaga analizy podatkowej (np. odsetki, różnice kursowe, wynagrodzenia — art. 15 ust. 4g/4h, art. 16 ust. 1 pkt 57 ustawy o CIT). Przyjęto wartości bilansowe.");
        if (rate == CitCalculator.ReducedRate)
            r.Warnings.Add("Zastosowano stawkę 9% (mały podatnik / pierwszy rok) — zweryfikuj spełnienie warunków art. 19 ust. 1 pkt 2 ustawy o CIT (m.in. limit przychodów, wyłączenie zysków kapitałowych).");
        r.Warnings.Add("Wyliczenie nie uwzględnia automatycznie: zysków kapitałowych (odrębne źródło), limitów kosztów finansowania dłużnego, strat z lat ubiegłych (wprowadź jako korektę), ulg — wymagana weryfikacja przez doradcę podatkowego.");
        return r;
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
        var fy = await _db.FiscalYears.Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
        var company = await _db.Companies.FirstAsync(c => c.Id == fy.CompanyId, ct);
        EnsureCitTaxpayer(company);
        if (company.CitAdvanceMethod == CitAdvanceMethod.Kwartalne && periodNumber % 3 != 0)
            throw new DomainException("Przy zaliczkach kwartalnych wylicza się je za okres kończący kwartał (3, 6, 9, 12).");
        var rec = await ReconcileAsync(fiscalYearId, periodNumber, ct);
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
