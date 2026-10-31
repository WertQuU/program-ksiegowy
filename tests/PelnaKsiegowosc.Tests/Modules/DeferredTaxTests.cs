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
        await cit.AddLossAsync("2025", new DateOnly(2025, 12, 31), false, 1000m, null);
        await cit.AddLossAsync("2025", new DateOnly(2025, 12, 31), true, 1000m, null);
        var svc = new DeferredTaxService(t.Db, t.User, t.Clock);

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
}
