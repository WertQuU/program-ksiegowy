using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Cit;

/// <summary>
/// Różnica przejściowa: dodatnia — do opodatkowania w przyszłości (rezerwa), ujemna — do odliczenia (aktywo).
/// Stawka — przewidywana na rok odwrócenia różnicy (art. 37 ust. 6 UoR); źródło „zyski kapitałowe” zawsze 19%.
/// </summary>
public sealed record TemporaryDifference(string Item, string Basis, decimal BookValue, decimal TaxValue, decimal Difference,
    DateOnly ReversalPeriodStart = default, bool CapitalGains = false, decimal RatePercent = 0m)
{
    public decimal Tax => Money.Round(Math.Abs(Difference) * RatePercent / 100m);
    /// <summary>Rok kalendarzowy początku roku podatkowego, w którym różnica się odwraca (do prezentacji).</summary>
    public int ReversalYear => ReversalPeriodStart.Year;
}

public sealed record DeferredTaxCalculation(DateOnly AsOf, decimal RatePercent, IReadOnlyList<TemporaryDifference> Differences,
    decimal TaxableDifferences, decimal DeductibleDifferences, decimal Provision, decimal Asset, decimal BookedProvision, decimal BookedAsset,
    IReadOnlyList<string>? Warnings = null)
{
    public decimal ProvisionChange => Provision - BookedProvision;
    public decimal AssetChange => Asset - BookedAsset;
    /// <summary>Obciążenie wyniku podatkiem odroczonym (dodatnie — koszt, ujemne — zmniejszenie obciążenia).</summary>
    public decimal ResultCharge => ProvisionChange - AssetChange;
}

/// <summary>
/// Podatek odroczony (art. 37 UoR): rezerwa (841) i aktywo (842) od różnic przejściowych między wartością bilansową
/// a podatkową — środki trwałe (amortyzacja bilansowa ≠ podatkowa), odpisy aktualizujące należności i rezerwy na
/// zobowiązania (koszty niepodatkowe do czasu realizacji), straty podatkowe do rozliczenia. Zmiana obciąża wynik (872,
/// RZiS „podatek dochodowy”). Jednostki małe/mikro mogą nie ustalać podatku odroczonego (art. 37 ust. 10 UoR).
/// </summary>
public sealed class DeferredTaxService
{
    public const string ProvisionAccount = "841";
    public const string AssetAccount = "842";
    public const string ExpenseAccount = "872";

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    /// <summary>Prognoza stawki dla roku podatkowego rozpoczynającego się w dniu <paramref name="taxYearStart"/>.</summary>
    public static string ExpectedRateKey(DateOnly taxYearStart) => $"cit.prognozaStawki.{taxYearStart:yyyy-MM-dd}";

    /// <summary>Prognozowana stawka CIT (inne źródła) na rok odwrócenia różnic — decyzja kierownika jednostki z uzasadnieniem.</summary>
    public async Task SetExpectedRateAsync(DateOnly taxYearStart, decimal ratePercent, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (ratePercent is not (CitCalculator.ReducedRate or CitCalculator.StandardRate)) throw new DomainException("Stawka CIT: 9% albo 19%.");
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Uzasadnij prognozę (np. przewidywane przychody i status małego podatnika w roku odwrócenia różnic).");
        await new Setup.SettingsService(_db, _clock).SetAsync<decimal?>(_user.CompanyId, ExpectedRateKey(taxYearStart), ratePercent, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_PROGNOZA_STAWKI", "AppSetting", ExpectedRateKey(taxYearStart), $"rok podatkowy od {taxYearStart.ToPl()}: {ratePercent}% — {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public static string LossAssessmentKey(Guid lossId) => $"cit.aktywoStrata.{lossId}";
    public sealed record LossAssessment(bool Recoverable, decimal? RecoverableAmount, string Justification, DateOnly AssessedOn);

    /// <summary>
    /// Udokumentowana ocena możliwości wykorzystania straty (art. 37 ust. 4 UoR, zasada ostrożności): aktywo ze straty ujmuje się
    /// tylko w kwocie, co do której prawdopodobne jest osiągnięcie dochodu do jej odliczenia (np. prognoza wyników, kontrakty).
    /// </summary>
    public async Task AssessLossAsync(Guid lossId, bool recoverable, decimal? recoverableAmount, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Opisz podstawę oceny (prognoza dochodów, zawarte umowy, plan finansowy).");
        if (recoverableAmount is < 0) throw new DomainException("Kwota nie może być ujemna.");
        await _db.TaxLosses.FirstAsync(l => l.Id == lossId && l.CompanyId == _user.CompanyId, ct);
        await new Setup.SettingsService(_db, _clock).SetAsync<LossAssessment?>(_user.CompanyId, LossAssessmentKey(lossId),
            new LossAssessment(recoverable, recoverableAmount, justification.Trim(), _clock.Today), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "POD_ODROCZONY_OCENA_STRATY", "TaxLoss", lossId.ToString(),
            $"{(recoverable ? "do wykorzystania" : "niepewna")}{(recoverableAmount is { } a ? $" do {Money.Xml(a)}" : "")}: {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public DeferredTaxService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    /// <summary>
    /// Kolejne lata podatkowe od dnia bilansowego: bieżący (gdy <paramref name="asOf"/> jest przed jego końcem), lata już utworzone
    /// w programie, dalej — o długości bieżącego roku (prognoza).
    /// </summary>
    public static List<(DateOnly Start, DateOnly End)> FuturePeriods(Domain.Periods.FiscalYear fy, DateOnly asOf, IReadOnlyList<(DateOnly Start, DateOnly End)> years)
    {
        var periods = new List<(DateOnly Start, DateOnly End)>();
        if (asOf < fy.EndDate) periods.Add((fy.StartDate, fy.EndDate));
        var months = Math.Max(1, (fy.EndDate.Year - fy.StartDate.Year) * 12 + fy.EndDate.Month - fy.StartDate.Month + 1);
        var next = fy.EndDate.AddDays(1);
        for (var i = 0; i < 60; i++)
        {
            var known = years.FirstOrDefault(y => y.Start == next);
            var end = known != default ? known.End : next.AddMonths(months).AddDays(-1);
            periods.Add((next, end));
            next = end.AddDays(1);
        }
        return periods;
    }

    /// <summary>
    /// Odwrócenie istniejącej różnicy <paramref name="difference"/> w kolejnych latach na podstawie przyszłych zmian różnicy
    /// (odpis bilansowy − odpis podatkowy): różnica istniejąca odwraca się dopiero wtedy, gdy narastająco spada poniżej
    /// dotychczasowego minimum — nowe różnice powstające po dniu bilansowym (np. wyższa amortyzacja podatkowa) nie są
    /// odwróceniem różnicy istniejącej. Część nieodwrócona w horyzoncie planu — w ostatnim roku z przepływem.
    /// </summary>
    public static List<((DateOnly Start, DateOnly End) Period, decimal Amount)> ExistingDifferenceReversal(decimal difference,
        IReadOnlyList<((DateOnly Start, DateOnly End) Period, decimal Reversal)> flows)
    {
        var result = new List<((DateOnly, DateOnly), decimal)>();
        if (difference == 0 || flows.Count == 0) return result;
        var sign = Math.Sign(difference);
        var existing = Math.Abs(difference);
        var running = existing;
        foreach (var (period, reversal) in flows)
        {
            running -= sign * reversal;
            var stillExisting = Math.Min(existing, Math.Max(0m, running));
            var reversed = existing - stillExisting;
            if (reversed > 0) result.Add((period, sign * reversed));
            existing = stillExisting;
            if (existing == 0) break;
        }
        if (existing > 0)
        {
            var last = flows.LastOrDefault(f => f.Reversal != 0);
            result.Add((last == default ? flows[0].Period : last.Period, sign * existing));
        }
        return result;
    }

    public async Task<DeferredTaxCalculation> ComputeAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.AsNoTracking().FirstAsync(f => f.Id == fiscalYearId, ct);
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == fy.CompanyId, ct);
        if (!LegalForms.IsCitTaxpayer(company.LegalForm))
            throw new DomainException("Jednostka nie jest podatnikiem CIT — podatku odroczonego nie ustala się (podatek rozliczają wspólnicy / właściciel).");
        var warnings = new List<string>();
        var settings = new Setup.SettingsService(_db, _clock);
        var years = (await _db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == fy.CompanyId).Select(f => new { f.StartDate, f.EndDate }).ToListAsync(ct))
            .Select(y => (y.StartDate, y.EndDate)).ToList();
        var periods = FuturePeriods(fy, asOf, years);
        var first = periods[0];
        // stawka bieżącego roku (wyliczenie CIT) — dla odwrócenia w bieżącym roku i jako punkt wyjścia, gdy brak prognozy
        decimal current;
        try
        {
            var fyFull = await _db.FiscalYears.AsNoTracking().Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
            var rec = await new CitService(_db, _user, _clock).ReconcileAsync(fiscalYearId, fyFull.Periods.Count, ct);
            current = rec.Rate.RateMissing ? CitCalculator.StandardRate : rec.Rate.RatePercent;
        }
        catch (DomainException) { current = CitCalculator.StandardRate; }
        var expectedCache = new Dictionary<DateOnly, decimal>();
        async Task<decimal> RateForAsync(DateOnly periodStart, bool capital)
        {
            if (capital) return CitCalculator.StandardRate;
            if (periodStart == fy.StartDate) return current;
            if (expectedCache.TryGetValue(periodStart, out var r)) return r;
            var expected = await settings.GetAsync<decimal?>(fy.CompanyId, ExpectedRateKey(periodStart), () => null, ct);
            if (expected is null)
            {
                var label = periods.First(p => p.Start == periodStart);
                warnings.Add($"Brak prognozy stawki CIT na rok podatkowy {label.Start.ToPl()}–{label.End.ToPl()} (odwrócenie różnic) — przyjęto {current:0}% jak w bieżącym roku. Potwierdź prognozę (art. 37 ust. 6 UoR): 9% tylko przy spełnieniu warunków art. 19 w tym roku.");
                expected = current;
            }
            expectedCache[periodStart] = expected.Value;
            return expected.Value;
        }
        var rate = await RateForAsync(first.Start, false);
        var diffs = new List<TemporaryDifference>();

        // środki trwałe: różnica = wartość bilansowa netto − podatkowa netto; odwraca się w latach, w których przyszłe odpisy
        // bilansowe i podatkowe (wg planów amortyzacji, z ulepszeniami i metodą degresywną) się różnią: odwrócenie w roku =
        // odpis bilansowy − odpis podatkowy; reszta (różnice planu i odpisów zaksięgowanych) — w ostatnim roku harmonogramu
        var assets = await _db.FixedAssets.AsNoTracking().Include(a => a.Operations).Where(a => a.CompanyId == fy.CompanyId && a.PlacedInServiceDate <= asOf
                                                                     && (a.DisposalDate == null || a.DisposalDate > asOf)).ToListAsync(ct);
        var charges = (await _db.DepreciationCharges.AsNoTracking().Where(c => c.CompanyId == fy.CompanyId && c.JournalEntryId != null).ToListAsync(ct))
            .Where(c => new DateOnly(c.Year, c.Month, 1) <= asOf).GroupBy(c => c.FixedAssetId)
            .ToDictionary(g => g.Key, g => (Book: g.Sum(c => c.BookAmount), Tax: g.Sum(c => c.TaxAmount)));
        var asOfMonth = new DateOnly(asOf.Year, asOf.Month, 1);
        foreach (var a in assets)
        {
            var c = charges.GetValueOrDefault(a.Id);
            var book = a.InitialValueBook - a.OpeningAccumulatedBook - c.Book;
            var tax = a.InitialValueTax - a.OpeningAccumulatedTax - c.Tax;
            if (book == tax) continue;
            var total = Money.Round(book - tax);
            var bookPlan = Domain.FixedAssets.DepreciationCalculator.Plan(a, Domain.FixedAssets.DepreciationBasis.Bilansowa).Where(r => new DateOnly(r.Year, r.Month, 1) > asOfMonth).ToList();
            var taxPlan = Domain.FixedAssets.DepreciationCalculator.Plan(a, Domain.FixedAssets.DepreciationBasis.Podatkowa).Where(r => new DateOnly(r.Year, r.Month, 1) > asOfMonth).ToList();
            decimal In(IEnumerable<Domain.FixedAssets.DepreciationPlanRow> rows, (DateOnly Start, DateOnly End) p) =>
                rows.Where(r => new DateOnly(r.Year, r.Month, 1) >= new DateOnly(p.Start.Year, p.Start.Month, 1) && new DateOnly(r.Year, r.Month, 1) <= p.End).Sum(r => r.Amount);
            var flows = periods.Select(p => (Period: p, Reversal: Money.Round(In(bookPlan, p) - In(taxPlan, p)))).ToList();
            foreach (var (p, part) in ExistingDifferenceReversal(total, flows))
                diffs.Add(new TemporaryDifference($"Środek trwały {a.InventoryNumber} {a.Name} — odwrócenie w roku {p.Start.ToPl()}–{p.End.ToPl()}",
                    "wartość bilansowa ≠ podatkowa (plan amortyzacji)", book, tax, part, p.Start, false, await RateForAsync(p.Start, false)));
        }

        // odpisy aktualizujące należności i rezerwy (NKUP) — odwrócenie w najbliższym roku podatkowym po dniu bilansowym
        var allowance = (await _db.ReceivableAllowances.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId && a.Date <= asOf).Select(a => a.Amount).ToListAsync(ct)).Sum();
        if (allowance != 0) diffs.Add(new TemporaryDifference("Odpisy aktualizujące należności", "art. 16 ust. 1 pkt 26a ustawy o CIT", -allowance, 0m, -allowance, first.Start, false, rate));
        var provisions = await _db.Provisions.AsNoTracking().Where(p => p.CompanyId == fy.CompanyId && p.Date <= asOf).ToListAsync(ct);
        foreach (var g in provisions.GroupBy(p => p.Kind))
        {
            var remaining = g.Sum(p => p.Remaining);
            if (remaining != 0) diffs.Add(new TemporaryDifference($"Rezerwy: {g.Key}", "art. 16 ust. 1 pkt 27 ustawy o CIT", -remaining, 0m, -remaining, first.Start, false, rate));
        }

        // straty podatkowe — wyłącznie z rejestru strat: okres odliczenia ze wspólnego mechanizmu lat podatkowych; aktywo tylko
        // po udokumentowanej ocenie możliwości wykorzystania (art. 37 ust. 4 UoR); zyski kapitałowe po 19%
        var losses = await _db.TaxLosses.AsNoTracking().Where(l => l.CompanyId == fy.CompanyId && l.YearEnd <= asOf).ToListAsync(ct);
        var usedByLoss = (await _db.TaxLossDeductions.AsNoTracking().Where(d => d.CompanyId == fy.CompanyId).ToListAsync(ct))
            .GroupBy(d => d.TaxLossId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        foreach (var l in losses)
        {
            var left = l.Amount - usedByLoss.GetValueOrDefault(l.Id);
            if (left <= 0) continue;
            var until = TaxLossRules.WindowEnd(l, years);
            if (until is null) { warnings.Add($"Strata {l.YearLabel}: potwierdź koniec okresu odliczenia — bez tego aktywo nie jest ujmowane."); continue; }
            if (until < asOf.AddDays(1)) continue;
            var assessment = await settings.GetAsync<LossAssessment?>(fy.CompanyId, LossAssessmentKey(l.Id), () => null, ct);
            if (assessment is null) { warnings.Add($"Strata {l.YearLabel} ({Money.Format(left)} zł): brak udokumentowanej oceny możliwości jej wykorzystania — aktywo nie zostało ujęte (art. 37 ust. 4 UoR)."); continue; }
            if (!assessment.Recoverable) continue;
            var amount = Math.Min(left, assessment.RecoverableAmount ?? left);
            if (amount <= 0) continue;
            diffs.Add(new TemporaryDifference($"Strata podatkowa {l.YearLabel}{(l.CapitalGains ? " (zyski kapitałowe)" : "")}",
                $"art. 7 ust. 5 ustawy o CIT; ocena z {assessment.AssessedOn.ToPl()}: {assessment.Justification}", 0m, amount, -amount,
                first.Start, l.CapitalGains, await RateForAsync(first.Start, l.CapitalGains)));
        }

        var taxable = diffs.Where(d => d.Difference > 0).Sum(d => d.Difference);
        var deductible = -diffs.Where(d => d.Difference < 0).Sum(d => d.Difference);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, asOf, ct);
        decimal Bal(string n) => tb.Where(t => t.Account.Number == n).Sum(t => t.Balance);
        return new DeferredTaxCalculation(asOf, rate, diffs, taxable, deductible,
            diffs.Where(d => d.Difference > 0).Sum(d => d.Tax), diffs.Where(d => d.Difference < 0).Sum(d => d.Tax), -Bal(ProvisionAccount), Bal(AssetAccount), warnings);
    }

    /// <summary>Księguje zmianę rezerwy i aktywa: Wn 872 / Ma 841 (wzrost rezerwy), Wn 842 / Ma 872 (wzrost aktywa), odwrotnie przy spadku.</summary>
    public async Task<DeferredTaxCalculation> PostAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var c = await ComputeAsync(fiscalYearId, asOf, ct);
        if (c.ProvisionChange == 0 && c.AssetChange == 0) throw new DomainException("Podatek odroczony jest już ujęty w aktualnej wysokości.");
        await new ValuationService(_db, _user, _clock).EnsureAccountsAsync(asOf, ct, ProvisionAccount, AssetAccount, ExpenseAccount);
        var lines = new List<LineInput>();
        if (c.ProvisionChange > 0) lines.AddRange(new LineInput[] { new(ExpenseAccount, c.ProvisionChange, 0, Description: "Zwiększenie rezerwy z tytułu odroczonego podatku"), new(ProvisionAccount, 0, c.ProvisionChange) });
        if (c.ProvisionChange < 0) lines.AddRange(new LineInput[] { new(ProvisionAccount, -c.ProvisionChange, 0), new(ExpenseAccount, 0, -c.ProvisionChange, Description: "Zmniejszenie rezerwy z tytułu odroczonego podatku") });
        if (c.AssetChange > 0) lines.AddRange(new LineInput[] { new(AssetAccount, c.AssetChange, 0), new(ExpenseAccount, 0, c.AssetChange, Description: "Zwiększenie aktywa z tytułu odroczonego podatku") });
        if (c.AssetChange < 0) lines.AddRange(new LineInput[] { new(ExpenseAccount, -c.AssetChange, 0, Description: "Zmniejszenie aktywa z tytułu odroczonego podatku"), new(AssetAccount, 0, -c.AssetChange) });
        var number = $"POD/{await new NumberingService(_db).NextAsync(_user.CompanyId, $"POD/{asOf.Year}", ct)}/{asOf.Year}";
        await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", asOf, asOf, asOf,
            $"Podatek odroczony na {asOf.ToPl()} (stawki wg roku odwrócenia różnic: {string.Join(", ", c.Differences.Select(d => d.RatePercent).Distinct().Select(r => $"{r:0}%"))})", "PK", number, lines), ct);
        await new AuditService(_db, _clock).AddAsync(_user, "PODATEK_ODROCZONY", "FiscalYear", fiscalYearId.ToString(),
            $"rezerwa {Money.Xml(c.Provision)}; aktywo {Money.Xml(c.Asset)}", ct);
        await _db.SaveChangesAsync(ct);
        return c;
    }
}
