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
        var c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31), taxLossesToSettle: 1000m);
        Assert.Equal(6615m, c.DeductibleDifferences);
        Assert.Equal(0m, c.TaxableDifferences);
        var expected = Money.Round(6615m * c.RatePercent / 100m);
        Assert.Equal(expected, c.Asset);
        await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31), 1000m);
        Assert.Equal(expected, await t.BalanceAsync("842"));
        Assert.Equal(-expected, await t.BalanceAsync("872"));
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31), 1000m));

        // rozwiązanie części rezerwy — aktywo maleje
        var p = (await val.ProvisionsAsync()).Single();
        await val.UseProvisionAsync(p.Id, 2000m, new DateOnly(2026, 12, 31), release: true);
        var c2 = await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31), 1000m);
        Assert.Equal(Money.Round(4615m * c2.RatePercent / 100m), await t.BalanceAsync("842"));
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
        Assert.Contains(c.Warnings!, w => w.Contains("prognozy stawki CIT na 2027"));

        await svc.SetExpectedRateAsync(2027, 19m, "Przewidywane przychody 2027 powyżej limitu 2 mln euro");
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
        await svc.SetExpectedRateAsync(2027, 9m, "Mały podatnik — przychody 2027 poniżej limitu");
        await svc.SetExpectedRateAsync(2028, 19m, "Planowany wzrost przychodów ponad limit");
        var c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 2, 28));
        var parts = c.Differences.Where(d => d.Item.StartsWith("Środek trwały ST/1")).ToList();
        Assert.Equal(200m, parts.Sum(p => p.Difference));
        Assert.Equal(5, parts.Count);                                  // pozostała amortyzacja bilansowa ~5 lat
        Assert.Equal(Enumerable.Range(2027, 5), parts.Select(p => p.ReversalYear));
        Assert.Equal(9m, parts[0].RatePercent);
        Assert.Equal(19m, parts[1].RatePercent);
        Assert.Equal(parts.Sum(p => p.Tax), c.Provision);
    }
}
