using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class DeferredTaxTests
{
    [Fact]
    public async Task Deferred_tax_asset_from_provisions_and_allowances_is_posted_and_updated()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Dłużnik");
        await SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, cp.Id, 1000m, new DateOnly(2026, 1, 12));
        var val = new ValuationService(t.Db, t.User, t.Clock);
        await val.PostProposalsAsync(new DateOnly(2026, 9, 30)); // odpis 615
        await val.CreateProvisionAsync(ProvisionKind.NaKoszty, "Badanie sprawozdania", 5000m, new DateOnly(2026, 12, 31));

        var svc = new DeferredTaxService(t.Db, t.User, t.Clock);
        var c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(5615m, c.DeductibleDifferences); // straty wyłącznie z rejestru (z oceną) — brak ścieżki „spoza rejestru”
        Assert.Equal(0m, c.TaxableDifferences);
        var expected = Money.Round(5615m * c.RatePercent / 100m);
        Assert.Equal(expected, c.Asset);
        await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(expected, await t.BalanceAsync("842"));
        Assert.Equal(-expected, await t.BalanceAsync("872"));
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31)));

        // rozwiązanie części rezerwy — aktywo maleje
        var p = (await val.ProvisionsAsync()).Single();
        await val.UseProvisionAsync(p.Id, 2000m, new DateOnly(2026, 12, 31), release: true);
        var c2 = await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(Money.Round(3615m * c2.RatePercent / 100m), await t.BalanceAsync("842"));
    }

    [Fact]
    public async Task Rate_follows_reversal_year_forecast_and_capital_gains_losses_use_19_percent()
    {
        await using var t = await TestBooks.CreateAsync();
        var cit = new CitService(t.Db, t.User, t.Clock);
        await cit.SetLimitEurRateAsync(t.Year.StartDate, 4.2156m); // bieżący rok: 9% (pierwszy rok)
        var op = await cit.AddLossAsync("2025", new DateOnly(2025, 12, 31), false, 1000m, null);
        var capLoss = await cit.AddLossAsync("2025", new DateOnly(2025, 12, 31), true, 1000m, null);
        var svc = new DeferredTaxService(t.Db, t.User, t.Clock);
        // bez udokumentowanej oceny wykorzystania straty aktywa się nie ujmuje (art. 37 ust. 4 UoR)
        var none = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(0m, none.Asset);
        Assert.Contains(none.Warnings!, w => w.Contains("brak udokumentowanej oceny"));
        await svc.AssessLossAsync(op.Id, true, null, "Prognoza dochodu 2027–2028 z podpisanych umów");
        await svc.AssessLossAsync(capLoss.Id, true, null, "Planowana sprzedaż udziałów z zyskiem");

        var c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(9m, c.Differences.Single(d => !d.CapitalGains).RatePercent);   // brak prognozy — stawka bieżąca, z ostrzeżeniem
        Assert.Equal(19m, c.Differences.Single(d => d.CapitalGains).RatePercent);   // zyski kapitałowe zawsze 19%
        Assert.Equal(90m + 190m, c.Asset);
        Assert.Contains(c.Warnings!, w => w.Contains("prognozy stawki CIT na rok podatkowy 01.01.2027"));

        await svc.SetExpectedRateAsync(new DateOnly(2027, 1, 1), 19m, "Przewidywane przychody 2027 powyżej limitu 2 mln euro");
        var c2 = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(19m, c2.Differences.Single(d => !d.CapitalGains).RatePercent);
        Assert.Equal(190m + 190m, c2.Asset);
        Assert.DoesNotContain(c2.Warnings!, w => w.Contains("prognozy"));
    }

    [Fact]
    public async Task Fixed_asset_difference_is_spread_over_remaining_depreciation_years_with_rate_per_year()
    {
        await using var t = await TestBooks.CreateAsync();
        var fa = new Application.FixedAssets.FixedAssetService(t.Db, t.User, t.Clock);
        await fa.RegisterAsync(new Domain.FixedAssets.FixedAsset
        {
            InventoryNumber = "ST/1", Name = "Serwer", Category = Domain.FixedAssets.FixedAssetCategory.SrodekTrwaly, KstSymbol = "491",
            AcquisitionDate = new DateOnly(2026, 1, 10), PlacedInServiceDate = new DateOnly(2026, 1, 10),
            InitialValueBook = 24000m, InitialValueTax = 24000m, BookRatePercent = 20m, TaxRatePercent = 30m,
            AssetAccountNumber = "010-03", AccumulatedDepreciationAccountNumber = "070-03", ExpenseAccountNumber = "400"
        }, "080");
        await fa.RunDepreciationAsync(2026, 2); // bilansowo 400, podatkowo 600 → różnica 200 (rezerwa)
        var svc = new DeferredTaxService(t.Db, t.User, t.Clock);
        await svc.SetExpectedRateAsync(new DateOnly(2031, 1, 1), 9m, "Prognoza: mały podatnik w 2031 r.");
        var c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 2, 28));
        var parts = c.Differences.Where(d => d.Item.StartsWith("Środek trwały ST/1")).ToList();
        // amortyzacja podatkowa (30%) szybsza od bilansowej (20%): różnica 200 najpierw rośnie (lata 2026–2028), a istniejąca
        // część odwraca się dopiero, gdy amortyzacja podatkowa się skończy — w roku, w którym kończy się amortyzacja bilansowa (2031)
        Assert.Equal(200m, parts.Sum(p => p.Difference));
        Assert.Single(parts);
        Assert.Equal(new DateOnly(2031, 1, 1), parts[0].ReversalPeriodStart);
        Assert.Equal(9m, parts[0].RatePercent);
        Assert.Equal(parts.Sum(p => p.Tax), c.Provision);
    }

    [Fact]
    public void Reversal_periods_follow_tax_years_and_existing_difference_reverses_after_new_originations()
    {
        // rok podatkowy 1.07.2025–30.06.2026: odwrócenie od 1.07.2026 (nie od 2027 r.)
        var fy = new Domain.Periods.FiscalYear { StartDate = new DateOnly(2025, 7, 1), EndDate = new DateOnly(2026, 6, 30) };
        var periods = DeferredTaxService.FuturePeriods(fy, fy.EndDate, new List<(DateOnly, DateOnly)> { (fy.StartDate, fy.EndDate) });
        Assert.Equal((new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30)), periods[0]);
        Assert.Equal((new DateOnly(2027, 7, 1), new DateOnly(2028, 6, 30)), periods[1]);
        // przepływy: +60 (odwrócenie), −50 (nowa różnica), +100 (odwrócenie) przy istniejącej różnicy 100
        var flows = new List<((DateOnly, DateOnly), decimal)> { (periods[0], 60m), (periods[1], -50m), (periods[2], 100m) };
        var r = DeferredTaxService.ExistingDifferenceReversal(100m, flows);
        Assert.Equal(60m, r[0].Amount);   // pierwszy rok — odwrócenie 60
        Assert.Equal(40m, r[1].Amount);   // pozostałe 40 dopiero w 3. roku (rok 2. tworzy nową różnicę)
        Assert.Equal(periods[2], r[1].Period);
    }
}
