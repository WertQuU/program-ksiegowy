using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class FixedAssetDisposalSafetyTests
{
    [Fact]
    public async Task Failed_disposal_does_not_leak_disposal_date_into_later_save()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(new FixedAsset
        {
            InventoryNumber = "OT/SAFE-1", Name = "Urządzenie testowe",
            AcquisitionDate = new DateOnly(2026, 1, 2),
            PlacedInServiceDate = new DateOnly(2026, 1, 3),
            InitialValueBook = 12000m, InitialValueTax = 12000m,
            BookMethod = DepreciationMethod.Liniowa, BookRatePercent = 20m,
            TaxMethod = DepreciationMethod.Liniowa, TaxRatePercent = 20m
        }, offsetAccountNumber: null);

        // Luty wymaga odpisu amortyzacyjnego. Nieudana próba likwidacji
        // nie może pozostawić DisposalDate w śledzonym przez EF aktywie.
        var failure = await Assert.ThrowsAsync<DomainException>(() => service.DisposeAsync(
            asset.Id, new DateOnly(2026, 2, 10), isSale: false, reason: "Protokół LT"));
        Assert.Contains("Najpierw nalicz amortyzację", failure.Message);

        // Nowa, niezwiązana operacja wymusza zapis wszystkich śledzonych zmian.
        await books.AddCounterpartyAsync("POST", "Kontrola zapisu po błędzie");
        books.Db.ChangeTracker.Clear();

        var stored = await books.Db.FixedAssets.AsNoTracking()
            .Include(a => a.Operations).SingleAsync(a => a.Id == asset.Id);
        Assert.Null(stored.DisposalDate);
        Assert.Equal(FixedAssetStatus.WEwidencji, stored.Status);
        Assert.Single(stored.Operations);
    }

    [Fact]
    public async Task Disposal_before_placed_in_service_is_rejected_without_changing_asset()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(new FixedAsset
        {
            InventoryNumber = "OT/SAFE-2", Name = "Komputer testowy",
            AcquisitionDate = new DateOnly(2026, 1, 2),
            PlacedInServiceDate = new DateOnly(2026, 1, 3),
            InitialValueBook = 12000m, InitialValueTax = 12000m,
            BookMethod = DepreciationMethod.Liniowa, BookRatePercent = 20m,
            TaxMethod = DepreciationMethod.Liniowa, TaxRatePercent = 20m
        }, offsetAccountNumber: null);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.DisposeAsync(
            asset.Id, new DateOnly(2026, 1, 1), isSale: true, reason: "Zły termin"));
        Assert.Contains("nie może poprzedzać", error.Message);
        Assert.Null(asset.DisposalDate);
        Assert.Equal(FixedAssetStatus.WEwidencji, asset.Status);
    }

    [Fact]
    public async Task Rejected_registration_does_not_leave_ghost_asset_on_later_save()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = new FixedAsset
        {
            InventoryNumber = "OT/INVALID", Name = "Sprzęt bez dekretu",
            AcquisitionDate = new DateOnly(2026, 1, 2),
            PlacedInServiceDate = new DateOnly(2026, 1, 3),
            InitialValueBook = 12000m, InitialValueTax = 12000m,
            BookMethod = DepreciationMethod.Liniowa, BookRatePercent = 20m,
            TaxMethod = DepreciationMethod.Liniowa, TaxRatePercent = 20m
        };
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.RegisterAsync(asset, offsetAccountNumber: "NIEISTNIEJACE"));
        Assert.Contains("Konto przeciwstawne", error.Message);

        // Wcześniej stan Added pozostawał w kontekście i następny SaveChanges
        // mógł zapisać niezatwierdzony środek trwały z operacją OT bez dekretu.
        await books.AddCounterpartyAsync("NEXT", "Kolejny kontrahent");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.FixedAssets.AnyAsync(a => a.InventoryNumber == "OT/INVALID"));
        Assert.False(await books.Db.FixedAssetOperations.AnyAsync(o => o.FixedAssetId == asset.Id));
    }

    [Fact]
    public async Task Rejected_depreciation_journal_does_not_leave_phantom_charges_or_run()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(new FixedAsset
        {
            InventoryNumber = "OT/AM-ERR", Name = "Środek z amortyzacją",
            AcquisitionDate = new DateOnly(2026, 1, 2),
            PlacedInServiceDate = new DateOnly(2026, 1, 3),
            InitialValueBook = 12000m, InitialValueTax = 12000m,
            BookMethod = DepreciationMethod.Liniowa, BookRatePercent = 20m,
            TaxMethod = DepreciationMethod.Liniowa, TaxRatePercent = 20m
        }, offsetAccountNumber: null);

        // W testowych księgach jest tylko rok 2026. Próba wystawienia
        // dekretu AM na luty 2027 powinna zostać odrzucona.
        var error = await Assert.ThrowsAsync<DomainException>(() => service.RunDepreciationAsync(2027, 2));
        Assert.Contains("Brak roku obrotowego", error.Message);

        // Wymuś kolejny, niezwiązany zapis w tym samym DbContext.
        await books.AddCounterpartyAsync("AFTERAM", "Po odmowie naliczenia");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.DepreciationRuns.AnyAsync(r => r.Year == 2027 && r.Month == 2));
        Assert.False(await books.Db.DepreciationCharges.AnyAsync(c =>
            c.FixedAssetId == asset.Id && c.Year == 2027 && c.Month == 2));
        Assert.Equal(FixedAssetStatus.WEwidencji,
            (await books.Db.FixedAssets.AsNoTracking().SingleAsync(a => a.Id == asset.Id)).Status);
    }

    [Fact]
    public async Task Disposal_removes_full_tax_value_including_capitalized_improvements()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(new FixedAsset
        {
            InventoryNumber = "OT/TAX-IMPROV", Name = "Sprzęt po ulepszeniu",
            AcquisitionDate = new DateOnly(2026, 1, 2),
            PlacedInServiceDate = new DateOnly(2026, 1, 3),
            InitialValueBook = 12000m, InitialValueTax = 12000m,
            BookMethod = DepreciationMethod.Liniowa, BookRatePercent = 20m,
            TaxMethod = DepreciationMethod.Liniowa, TaxRatePercent = 20m
        }, offsetAccountNumber: "130-01");
        await service.AddImprovementAsync(asset.Id, new DateOnly(2026, 1, 20), 2000m,
            "130-01", "Ulepszenie udokumentowane");
        await service.RunDepreciationAsync(2026, 2);
        await service.DisposeAsync(asset.Id, new DateOnly(2026, 2, 28), isSale: false,
            reason: "Protokół LT/2026/02");

        books.Db.ChangeTracker.Clear();
        var stored = await books.Db.FixedAssets.AsNoTracking().Include(a => a.Operations)
            .SingleAsync(a => a.Id == asset.Id);
        var disposal = stored.Operations.Single(o => o.Type == FixedAssetOperationType.Likwidacja);
        Assert.Equal(-14000m, disposal.BookValueChange);
        Assert.Equal(-14000m, disposal.TaxValueChange);
        Assert.Equal(0m, stored.Operations.Sum(o => o.TaxValueChange));
        Assert.Equal(FixedAssetStatus.Zlikwidowany, stored.Status);
        Assert.Equal(new DateOnly(2026, 2, 28), stored.DisposalDate);
    }
}
