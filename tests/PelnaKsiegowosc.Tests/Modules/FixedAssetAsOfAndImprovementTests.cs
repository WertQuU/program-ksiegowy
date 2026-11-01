using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Wycena środka trwałego na konkretny dzień musi odpowiadać zaksięgowanym zdarzeniom.</summary>
public sealed class FixedAssetAsOfAndImprovementTests
{
    private static FixedAsset Asset(string inventoryNumber) => new()
    {
        InventoryNumber = inventoryNumber, Name = "Urządzenie",
        AcquisitionDate = new DateOnly(2026, 1, 2),
        PlacedInServiceDate = new DateOnly(2026, 1, 3),
        InitialValueBook = 12000m, InitialValueTax = 12000m,
        BookMethod = DepreciationMethod.Liniowa, BookRatePercent = 20m,
        TaxMethod = DepreciationMethod.Liniowa, TaxRatePercent = 20m
    };

    [Fact]
    public async Task Values_as_of_use_month_end_for_depreciation_and_zero_before_acquisition()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(Asset("OT/ASOF-1"), offsetAccountNumber: null);
        await service.RunDepreciationAsync(2026, 2);

        var before = await service.ValuesAsOfAsync(asset.Id, new DateOnly(2026, 1, 1));
        Assert.Equal((0m, 0m, 0m, 0m), before);

        var februaryFirst = await service.ValuesAsOfAsync(asset.Id, new DateOnly(2026, 2, 1));
        Assert.Equal((12000m, 0m, 0m, 12000m), februaryFirst);
        var februaryLast = await service.ValuesAsOfAsync(asset.Id, new DateOnly(2026, 2, 28));
        Assert.Equal((12000m, 200m, 200m, 11800m), februaryLast);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Values_as_of_keep_prior_balance_but_zero_after_sale_or_liquidation(bool isSale)
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(Asset("OT/ASOF-2"), offsetAccountNumber: null);
        await service.RunDepreciationAsync(2026, 2);
        await service.DisposeAsync(asset.Id, new DateOnly(2026, 2, 28), isSale, "Protokół rozchodu");

        var before = await service.ValuesAsOfAsync(asset.Id, new DateOnly(2026, 2, 27));
        Assert.Equal((12000m, 0m, 0m, 12000m), before);
        var after = await service.ValuesAsOfAsync(asset.Id, new DateOnly(2026, 2, 28));
        Assert.Equal((0m, 0m, 0m, 0m), after);
        Assert.Equal((0m, 0m, 0m, 0m),
            await service.ValuesAsOfAsync(asset.Id, new DateOnly(2026, 3, 15)));
    }

    [Fact]
    public async Task Cannot_backdate_improvement_into_month_with_already_calculated_depreciation()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(Asset("OT/BACKDATE"), offsetAccountNumber: null);
        await service.RunDepreciationAsync(2026, 2);

        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            service.AddImprovementAsync(asset.Id, new DateOnly(2026, 1, 20), 1000m,
                "130-01", "Ulepszenie wykryte po zamknięciu lutego"));
        Assert.Contains("korekta odpisów", denied.Message);

        await books.AddCounterpartyAsync("AFTER", "Po odrzuconym ulepszeniu");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.FixedAssetOperations
            .AnyAsync(o => o.FixedAssetId == asset.Id && o.Type == FixedAssetOperationType.Ulepszenie));

        // Ulepszenie lutowe wpływa dopiero na marzec, dlatego nie zmienia
        // już naliczonego odpisu za luty.
        await service.AddImprovementAsync(asset.Id, new DateOnly(2026, 2, 20), 1000m,
            "130-01", "Ulepszenie z dokumentem lutowym");
        Assert.Equal(13000m,
            (await service.ValuesAsOfAsync(asset.Id, new DateOnly(2026, 2, 28))).gross);
    }

    [Fact]
    public async Task Improvement_requires_document_reason_and_cannot_precede_use()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var asset = await service.RegisterAsync(Asset("OT/IMPROVE-VALIDATION"), offsetAccountNumber: null);
        await Assert.ThrowsAsync<DomainException>(() =>
            service.AddImprovementAsync(asset.Id, new DateOnly(2026, 1, 2), 1000m,
                "130-01", "Zbyt wczesna data"));
        await Assert.ThrowsAsync<DomainException>(() =>
            service.AddImprovementAsync(asset.Id, new DateOnly(2026, 1, 5), 1000m,
                "130-01", " "));
        Assert.False(await books.Db.FixedAssetOperations
            .AnyAsync(o => o.FixedAssetId == asset.Id && o.Type == FixedAssetOperationType.Ulepszenie));
    }
}
