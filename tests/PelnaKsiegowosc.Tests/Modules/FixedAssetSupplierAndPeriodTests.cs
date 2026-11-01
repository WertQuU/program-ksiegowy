using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Powiązania z dostawcami i parametry amortyzacji pod kontrolą jednostki.</summary>
public sealed class FixedAssetSupplierAndPeriodTests
{
    private static FixedAsset Asset(string number) => new()
    {
        InventoryNumber = number,
        Name = "Środek trwały testowy",
        AcquisitionDate = new DateOnly(2026, 1, 2),
        PlacedInServiceDate = new DateOnly(2026, 1, 3),
        InitialValueBook = 12000m,
        InitialValueTax = 12000m,
        BookMethod = DepreciationMethod.Liniowa,
        BookRatePercent = 20m,
        TaxMethod = DepreciationMethod.Liniowa,
        TaxRatePercent = 20m
    };

    [Fact]
    public async Task Registration_cannot_link_asset_to_supplier_from_another_company()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var foreignCompany = new Company
        {
            FullName = "Obca spółka", ShortName = "Obca", Nip = "1234567802"
        };
        books.Db.Companies.Add(foreignCompany);
        var foreignSupplier = new Counterparty
        {
            CompanyId = foreignCompany.Id, Code = "SUP-FOREIGN", Name = "Dostawca z innej spółki"
        };
        books.Db.Counterparties.Add(foreignSupplier);
        await books.Db.SaveChangesAsync();

        var asset = Asset("OT/SUP-FOREIGN");
        asset.SupplierId = foreignSupplier.Id;
        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            service.RegisterAsync(asset, offsetAccountNumber: null));
        Assert.Contains("aktualnej jednostki", denied.Message);

        // Nawet gdy formularz nie wskazuje SupplierId, oddzielny argument
        // kontrahenta do zapisu OT podlega dokładnie tej samej kontroli.
        var otherDenied = await Assert.ThrowsAsync<DomainException>(() =>
            service.RegisterAsync(Asset("OT/SUP-OFFSET"), offsetAccountNumber: null,
                supplierCounterpartyId: foreignSupplier.Id));
        Assert.Contains("aktualnej jednostki", otherDenied.Message);

        await books.AddCounterpartyAsync("LATER", "Zapis po błędnym przyjęciu");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.FixedAssets.AsNoTracking()
            .AnyAsync(a => a.InventoryNumber == "OT/SUP-FOREIGN" ||
                a.InventoryNumber == "OT/SUP-OFFSET"));
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(0, 2)]
    [InlineData(10000, 2)]
    public async Task Depreciation_rejects_invalid_calendar_period_as_domain_error(int year, int month)
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new FixedAssetService(books.Db, books.User, books.Clock);
        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.RunDepreciationAsync(year, month));
        Assert.Contains("poprawny rok", error.Message);
        Assert.Empty(await books.Db.DepreciationRuns.AsNoTracking().ToListAsync());
    }
}
