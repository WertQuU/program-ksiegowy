using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Settlements;
using Microsoft.EntityFrameworkCore;
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
        // odwrócenie w 2027 r. — bez zatwierdzonej prognozy stawki i granic roku istotna kwota nie jest księgowana
        Assert.Equal(c.Asset, c.UnconfirmedTax);
        var blocked = await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31)));
        Assert.Contains("niezatwierdzonych prognozach", blocked.Message);
        await svc.SetExpectedRateAsync(new DateOnly(2027, 1, 1), 19m, "Prognoza przychodów 2027 powyżej limitu");
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31))); // granice 2027 r. nadal niepotwierdzone
        await svc.ConfirmTaxYearCalendarAsync(new DateOnly(2035, 12, 31), "Rok podatkowy = kalendarzowy, brak planowanej zmiany (umowa spółki)");
        c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        // odpis na należność i istotna rezerwa na koszty (950 zł podatku > próg 0 zł) — bez kwalifikacji
        Assert.Equal(Money.Round(5615m * 19m / 100m), c.UnconfirmedTax);
        Assert.Contains(c.Warnings!, w => w.Contains("Odpis aktualizujący należność") && w.Contains("brak kwalifikacji"));
        Assert.Contains(c.Warnings!, w => w.Contains("Badanie sprawozdania") && w.Contains("brak kwalifikacji"));
        await svc.QualifyDifferenceAsync((await val.ProvisionsAsync()).Single().Id, true, null, "Umowa z biegłym — zapłata w I kwartale 2027 r., koszt podatkowy w dniu poniesienia");
        var item = (await t.Db.ReceivableAllowances.FirstAsync()).OpenItemId;
        await svc.QualifyDifferenceAsync(item, true, null, "Dłużnik w restrukturyzacji — nieściągalność zostanie udokumentowana w 2027 r. (art. 16 ust. 1 pkt 25)");
        c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(0m, c.UnconfirmedTax);
        expected = Money.Round(5615m * 19m / 100m);
        Assert.Equal(expected, c.Asset);
        await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(expected, await t.BalanceAsync("842"));
        Assert.Equal(-expected, await t.BalanceAsync("872"));
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31)));

        // rozwiązanie części rezerwy — aktywo maleje
        var p = (await val.ProvisionsAsync()).Single();
        await val.UseProvisionAsync(p.Id, 2000m, new DateOnly(2026, 12, 31), release: true);
        var c2 = await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(Money.Round(3615m * 19m / 100m), await t.BalanceAsync("842"));
    }

    [Fact]
    public async Task Provisions_are_qualified_individually_permanent_ones_are_excluded_and_long_term_ones_reverse_later()
    {
        await using var t = await TestBooks.CreateAsync();
        var val = new ValuationService(t.Db, t.User, t.Clock);
        var penalty = await val.CreateProvisionAsync(ProvisionKind.Inna, "Kara umowna za opóźnienie dostawy", 10000m, new DateOnly(2026, 12, 31));
        var pension = await val.CreateProvisionAsync(ProvisionKind.Emerytalna, "Odprawy emerytalne", 20000m, new DateOnly(2026, 12, 31));
        await val.CreateProvisionAsync(ProvisionKind.Urlopowa, "Niewykorzystane urlopy", 3000m, new DateOnly(2026, 12, 31));
        var svc = new DeferredTaxService(t.Db, t.User, t.Clock);
        await svc.ConfirmTaxYearCalendarAsync(new DateOnly(2040, 12, 31), "Rok podatkowy = kalendarzowy");
        foreach (var y in new[] { 2027, 2031 }) await svc.SetExpectedRateAsync(new DateOnly(y, 1, 1), 19m, "Przychody powyżej limitu 2 mln euro");
        var c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(3, c.Differences.Count);
        Assert.Equal(Money.Round(33000m * 0.19m), c.UnconfirmedTax);                    // wszystkie istotne (próg 0 zł) — do kwalifikacji
        await svc.SetMaterialityAsync(600m, "Polityka rachunkowości: próg istotności 600 zł podatku");
        c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(Money.Round(30000m * 0.19m), c.UnconfirmedTax);                    // urlopowa 570 zł ≤ próg — domyślnie; kara i odprawy — zawsze
        await Assert.ThrowsAsync<DomainException>(() => svc.QualifyDifferenceAsync(penalty.Id, false, null, ""));
        await svc.QualifyDifferenceAsync(penalty.Id, false, null, "Kara umowna za zwłokę w dostawie — art. 16 ust. 1 pkt 22 ustawy o CIT, nigdy KUP");
        await svc.QualifyDifferenceAsync(pension.Id, true, new DateOnly(2031, 1, 1), "Przejście pracownika na emeryturę w 2031 r.");
        c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.DoesNotContain(c.Differences, d => d.Item.Contains("Kara umowna"));        // różnica trwała — bez aktywa
        Assert.Equal(new DateOnly(2031, 1, 1), c.Differences.Single(d => d.Item.Contains("emerytalne")).ReversalPeriodStart);
        Assert.Equal(0m, c.UnconfirmedTax);
        Assert.Equal(Money.Round(23000m * 0.19m), c.Asset);
    }

    [Fact]
    public async Task Materiality_threshold_allows_posting_immaterial_unconfirmed_amounts()
    {
        await using var t = await TestBooks.CreateAsync();
        var val = new ValuationService(t.Db, t.User, t.Clock);
        await val.CreateProvisionAsync(ProvisionKind.NaKoszty, "Badanie sprawozdania", 1000m, new DateOnly(2026, 12, 31));
        var svc = new DeferredTaxService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31)));
        await Assert.ThrowsAsync<DomainException>(() => svc.SetMaterialityAsync(500m, " "));
        await svc.SetMaterialityAsync(500m, "Polityka rachunkowości: próg istotności 500 zł podatku");
        var c = await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31));   // 1000 × 19% (lub 9%) < 500 — dopuszczalne
        Assert.True(c.UnconfirmedTax is > 0 and <= 500m);
        Assert.Equal(1, await t.Db.AuditEvents.CountAsync(a => a.Action == "POD_ODROCZONY_PROG"));
    }

    [Fact]
    public async Task Short_first_tax_year_does_not_shorten_forecast_years_and_unknown_years_are_flagged()
    {
        // pierwszy rok podatkowy 1.07–31.12.2026 (6 miesięcy): kolejne lata — 12 miesięcy, nie 6
        var fy = new Domain.Periods.FiscalYear { StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2026, 12, 31) };
        var periods = DeferredTaxService.FuturePeriods(fy, fy.EndDate, new List<(DateOnly, DateOnly)> { (fy.StartDate, fy.EndDate) }, out var projected);
        Assert.Equal((new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31)), periods[0]);
        Assert.Equal((new DateOnly(2028, 1, 1), new DateOnly(2028, 12, 31)), periods[1]);
        Assert.Equal(new DateOnly(2027, 1, 1), projected);
        // rok zmieniony założony w programie (art. 8 ust. 6: 1.01.2027–30.06.2028) — przyjmowany dokładnie, kolejne od 1.07.2028
        var known = new List<(DateOnly, DateOnly)> { (fy.StartDate, fy.EndDate), (new DateOnly(2027, 1, 1), new DateOnly(2028, 6, 30)) };
        periods = DeferredTaxService.FuturePeriods(fy, fy.EndDate, known, out projected, calendarConfirmedThrough: new DateOnly(2029, 6, 30));
        Assert.Equal((new DateOnly(2027, 1, 1), new DateOnly(2028, 6, 30)), periods[0]);
        Assert.Equal((new DateOnly(2028, 7, 1), new DateOnly(2029, 6, 30)), periods[1]);
        Assert.Equal(new DateOnly(2029, 7, 1), projected);   // dalej — niepotwierdzone
    }

    [Fact]
    public void Unexplained_part_of_difference_is_not_forced_into_last_year()
    {
        var p1 = (new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var p2 = (new DateOnly(2028, 1, 1), new DateOnly(2028, 12, 31));
        var flows = new List<((DateOnly, DateOnly), decimal)> { (p1, 30m), (p2, 20m) };
        var r = DeferredTaxService.ExistingDifferenceReversal(100m, flows, out var unexplained);
        Assert.Equal(new[] { 30m, 20m }, r.Select(x => x.Amount));
        Assert.Equal(50m, unexplained);                                       // nie trafia do 2028 r.
        r = DeferredTaxService.ExistingDifferenceReversal(-50.4m, new List<((DateOnly, DateOnly), decimal)> { (p1, -50m) }, out unexplained);
        Assert.Equal(-50.4m, r.Single().Amount);                               // zaokrąglenie < 1 zł — w ostatnim roku
        Assert.Equal(0m, unexplained);
        DeferredTaxService.ExistingDifferenceReversal(80m, new List<((DateOnly, DateOnly), decimal)>(), out unexplained);
        Assert.Equal(80m, unexplained);
    }

    [Fact]
    public async Task Fixed_asset_difference_outside_plan_requires_verification_or_disposal_year()
    {
        await using var t = await TestBooks.CreateAsync();
        var fa = new Application.FixedAssets.FixedAssetService(t.Db, t.User, t.Clock);
        // grunt niepodlegający amortyzacji podatkowej, a bilansowo wyceniany inaczej (np. inna wartość początkowa) — różnica nie odwraca się planem
        var asset = await fa.RegisterAsync(new Domain.FixedAssets.FixedAsset
        {
            InventoryNumber = "GR/1", Name = "Działka", Category = Domain.FixedAssets.FixedAssetCategory.SrodekTrwaly, KstSymbol = "010",
            AcquisitionDate = new DateOnly(2026, 1, 10), PlacedInServiceDate = new DateOnly(2026, 1, 10),
            InitialValueBook = 100000m, InitialValueTax = 90000m, BookMethod = Domain.FixedAssets.DepreciationMethod.Brak, TaxMethod = Domain.FixedAssets.DepreciationMethod.Brak,
            AssetAccountNumber = "010-03", AccumulatedDepreciationAccountNumber = "070-03", ExpenseAccountNumber = "400"
        }, "080");
        var svc = new DeferredTaxService(t.Db, t.User, t.Clock);
        var c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        var item = c.Differences.Single(d => d.Item.Contains("GR/1"));
        Assert.True(item.ReversalUnknown);
        Assert.True(item.Unconfirmed);
        Assert.Equal(10000m, item.Difference);
        Assert.Contains(c.Warnings!, w => w.Contains("GR/1") && w.Contains("nie odwraca się"));
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31)));

        // udokumentowana decyzja o sprzedaży w 2029 r. → odwrócenie w tym roku, stawka wg prognozy na 2029 r.
        await svc.SetReversalYearAsync(asset.Id, new DateOnly(2029, 1, 1), "Uchwała zarządu: sprzedaż działki w 2029 r.");
        await svc.SetExpectedRateAsync(new DateOnly(2029, 1, 1), 19m, "Przychody 2029 powyżej limitu");
        await svc.ConfirmTaxYearCalendarAsync(new DateOnly(2030, 12, 31), "Rok podatkowy = kalendarzowy");
        c = await svc.ComputeAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        item = c.Differences.Single(d => d.Item.Contains("GR/1"));
        Assert.Equal(new DateOnly(2029, 1, 1), item.ReversalPeriodStart);
        Assert.False(item.Unconfirmed);
        Assert.Equal(1900m, item.Tax);
        await svc.PostAsync(t.Year.Id, new DateOnly(2026, 12, 31));
        Assert.Equal(-1900m, await t.BalanceAsync("841"));
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
        var periods = DeferredTaxService.FuturePeriods(fy, fy.EndDate, new List<(DateOnly, DateOnly)> { (fy.StartDate, fy.EndDate) }, out _);
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
