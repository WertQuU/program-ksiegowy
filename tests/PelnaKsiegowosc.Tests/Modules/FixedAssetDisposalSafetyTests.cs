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
}
