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
    int ReversalYear = 0, bool CapitalGains = false, decimal RatePercent = 0m)
{
    public decimal Tax => Money.Round(Math.Abs(Difference) * RatePercent / 100m);
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

    public static string ExpectedRateKey(int year) => $"cit.prognozaStawki.{year}";

    /// <summary>Prognozowana stawka CIT (inne źródła) na rok odwrócenia różnic — decyzja kierownika jednostki z uzasadnieniem.</summary>
    public async Task SetExpectedRateAsync(int year, decimal ratePercent, string justification, CancellationToken ct = default)
    {
        _user.Require(Permission.ManageCit);
        if (ratePercent is not (CitCalculator.ReducedRate or CitCalculator.StandardRate)) throw new DomainException("Stawka CIT: 9% albo 19%.");
        if (string.IsNullOrWhiteSpace(justification)) throw new DomainException("Uzasadnij prognozę (np. przewidywane przychody i status małego podatnika w roku odwrócenia różnic).");
        await new Setup.SettingsService(_db, _clock).SetAsync<decimal?>(_user.CompanyId, ExpectedRateKey(year), ratePercent, ct);
        await new AuditService(_db, _clock).AddAsync(_user, "CIT_PROGNOZA_STAWKI", "AppSetting", ExpectedRateKey(year), $"{year}: {ratePercent}% — {justification.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public DeferredTaxService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    /// <param name="taxLossesToSettle">Straty podatkowe możliwe do odliczenia w przyszłości (jeśli prawdopodobne jest osiągnięcie dochodu).</param>
    public async Task<DeferredTaxCalculation> ComputeAsync(Guid fiscalYearId, DateOnly asOf, decimal taxLossesToSettle = 0m, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.AsNoTracking().FirstAsync(f => f.Id == fiscalYearId, ct);
        var company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == fy.CompanyId, ct);
        if (!LegalForms.IsCitTaxpayer(company.LegalForm))
            throw new DomainException("Jednostka nie jest podatnikiem CIT — podatku odroczonego nie ustala się (podatek rozliczają wspólnicy / właściciel).");
        var warnings = new List<string>();
        var settings = new Setup.SettingsService(_db, _clock);
        var nextYear = fy.EndDate.Year + 1;
        // stawka bieżącego roku (wyliczenie CIT) — punkt wyjścia, gdy brak prognozy na rok odwrócenia
        decimal current;
        try
        {
            var fyFull = await _db.FiscalYears.AsNoTracking().Include(f => f.Periods).FirstAsync(f => f.Id == fiscalYearId, ct);
            var rec = await new CitService(_db, _user, _clock).ReconcileAsync(fiscalYearId, fyFull.Periods.Count, ct);
            current = rec.Rate.RateMissing ? CitCalculator.StandardRate : rec.Rate.RatePercent;
        }
        catch (DomainException) { current = CitCalculator.StandardRate; }
        var expectedCache = new Dictionary<int, decimal>();
        async Task<decimal> RateForAsync(int year, bool capital)
        {
            if (capital) return CitCalculator.StandardRate;
            if (expectedCache.TryGetValue(year, out var r)) return r;
            var expected = await settings.GetAsync<decimal?>(fy.CompanyId, ExpectedRateKey(year), () => null, ct);
            if (expected is null)
            {
                warnings.Add($"Brak prognozy stawki CIT na {year} r. (rok odwrócenia różnic) — przyjęto {current:0}% jak w bieżącym roku. Potwierdź prognozę (art. 37 ust. 6 UoR): 9% tylko przy spełnieniu warunków art. 19 w {year} r.");
                expected = current;
            }
            expectedCache[year] = expected.Value;
            return expected.Value;
        }
        var rate = await RateForAsync(nextYear, false);
        var diffs = new List<TemporaryDifference>();

        // środki trwałe: wartość bilansowa netto vs podatkowa netto (różnica odwraca się przez amortyzację — od kolejnego roku)
        var assets = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId && a.PlacedInServiceDate <= asOf
                                                                     && (a.DisposalDate == null || a.DisposalDate > asOf)).ToListAsync(ct);
        var charges = (await _db.DepreciationCharges.AsNoTracking().Where(c => c.CompanyId == fy.CompanyId && c.JournalEntryId != null).ToListAsync(ct))
            .Where(c => new DateOnly(c.Year, c.Month, 1) <= asOf).GroupBy(c => c.FixedAssetId)
            .ToDictionary(g => g.Key, g => (Book: g.Sum(c => c.BookAmount), Tax: g.Sum(c => c.TaxAmount)));
        foreach (var a in assets)
        {
            var c = charges.GetValueOrDefault(a.Id);
            var book = a.InitialValueBook - a.OpeningAccumulatedBook - c.Book;
            var tax = a.InitialValueTax - a.OpeningAccumulatedTax - c.Tax;
            if (book != tax) diffs.Add(new TemporaryDifference($"Środek trwały {a.InventoryNumber} {a.Name}", "wartość bilansowa ≠ podatkowa (amortyzacja)", book, tax, Money.Round(book - tax), nextYear, false, rate));
        }

        // odpisy aktualizujące należności (NKUP do czasu uprawdopodobnienia nieściągalności)
        var allowance = (await _db.ReceivableAllowances.AsNoTracking().Where(a => a.CompanyId == fy.CompanyId && a.Date <= asOf).Select(a => a.Amount).ToListAsync(ct)).Sum();
        if (allowance != 0) diffs.Add(new TemporaryDifference("Odpisy aktualizujące należności", "art. 16 ust. 1 pkt 26a ustawy o CIT", -allowance, 0m, -allowance, nextYear, false, rate));

        // rezerwy na zobowiązania (NKUP do czasu poniesienia kosztu)
        var provisions = await _db.Provisions.AsNoTracking().Where(p => p.CompanyId == fy.CompanyId && p.Date <= asOf).ToListAsync(ct);
        foreach (var g in provisions.GroupBy(p => p.Kind))
        {
            var remaining = g.Sum(p => p.Remaining);
            if (remaining != 0) diffs.Add(new TemporaryDifference($"Rezerwy: {g.Key}", "art. 16 ust. 1 pkt 27 ustawy o CIT", -remaining, 0m, -remaining, nextYear, false, rate));
        }

        // straty podatkowe z rejestru (art. 7 ust. 5) — pozostała kwota w okresie odliczenia; zyski kapitałowe po 19%
        var losses = await _db.TaxLosses.AsNoTracking().Where(l => l.CompanyId == fy.CompanyId && l.YearEnd <= asOf).ToListAsync(ct);
        var usedByLoss = (await _db.TaxLossDeductions.AsNoTracking().Where(d => d.CompanyId == fy.CompanyId).ToListAsync(ct))
            .GroupBy(d => d.TaxLossId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        foreach (var l in losses)
        {
            var left = l.Amount - usedByLoss.GetValueOrDefault(l.Id);
            if (left <= 0 || l.YearEnd.AddYears(5) < asOf) continue;
            diffs.Add(new TemporaryDifference($"Strata podatkowa {l.YearLabel}{(l.CapitalGains ? " (zyski kapitałowe)" : "")}", "art. 7 ust. 5 ustawy o CIT", 0m, left, -left,
                nextYear, l.CapitalGains, await RateForAsync(nextYear, l.CapitalGains)));
        }
        if (taxLossesToSettle > 0)
            diffs.Add(new TemporaryDifference("Straty podatkowe spoza rejestru", "art. 7 ust. 5 ustawy o CIT", 0m, taxLossesToSettle, -taxLossesToSettle, nextYear, false, rate));
        if (diffs.Any(d => d.Item.StartsWith("Strata")))
            warnings.Add("Aktywo ze strat podatkowych ujmuje się tylko, gdy prawdopodobne jest osiągnięcie dochodu do ich odliczenia (art. 37 ust. 4 UoR) — potwierdź w polityce.");

        var taxable = diffs.Where(d => d.Difference > 0).Sum(d => d.Difference);
        var deductible = -diffs.Where(d => d.Difference < 0).Sum(d => d.Difference);
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, asOf, ct);
        decimal Bal(string n) => tb.Where(t => t.Account.Number == n).Sum(t => t.Balance);
        return new DeferredTaxCalculation(asOf, rate, diffs, taxable, deductible,
            diffs.Where(d => d.Difference > 0).Sum(d => d.Tax), diffs.Where(d => d.Difference < 0).Sum(d => d.Tax), -Bal(ProvisionAccount), Bal(AssetAccount), warnings);
    }

    /// <summary>Księguje zmianę rezerwy i aktywa: Wn 872 / Ma 841 (wzrost rezerwy), Wn 842 / Ma 872 (wzrost aktywa), odwrotnie przy spadku.</summary>
    public async Task<DeferredTaxCalculation> PostAsync(Guid fiscalYearId, DateOnly asOf, decimal taxLossesToSettle = 0m, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var c = await ComputeAsync(fiscalYearId, asOf, taxLossesToSettle, ct);
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
