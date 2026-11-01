using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Warehouse;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Warehouse;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Odrzucony dokument nie może zmienić stanu ani ujawnić danych obcej jednostki.</summary>
public sealed class StockMutationAndTenantSafetyTests
{
    [Fact]
    public async Task Invalid_posting_account_does_not_change_inventory_on_later_save()
    {
        await using var books = await TestBooks.CreateAsync();
        var stock = new StockService(books.Db, books.User, books.Clock);
        var item = await stock.AddItemAsync(new StockItem
        {
            Code = "REG-1", Name = "Towar testowy", Kind = StockItemKind.Towar
        });
        var error = await Assert.ThrowsAsync<DomainException>(() => stock.RegisterAsync(
            StockMovementType.PZ, item.Id, new DateOnly(2026, 1, 10), 10m, 20m,
            offsetAccount: "KONTO-NIE-ISTNIEJE"));
        Assert.Contains("KONTO-NIE-ISTNIEJE", error.Message);

        // Wcześniej item.Quantity/Value zmieniały się przed walidacją dekretu
        // i mogły zostać zapisane przy następnej niezwiązanej operacji.
        await books.AddCounterpartyAsync("AFTER-STOCK", "Po nieudanym dokumencie magazynowym");
        books.Db.ChangeTracker.Clear();
        var stored = await books.Db.StockItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(0m, stored.Quantity);
        Assert.Equal(0m, stored.Value);
        Assert.False(await books.Db.StockMovements.AnyAsync(m => m.StockItemId == item.Id));
    }

    [Fact]
    public async Task Foreign_company_counterparty_is_rejected_even_without_ledger_posting()
    {
        await using var books = await TestBooks.CreateAsync();
        var stock = new StockService(books.Db, books.User, books.Clock);
        var item = await stock.AddItemAsync(new StockItem
        {
            Code = "REG-2", Name = "Towar testowy", Kind = StockItemKind.Towar
        });
        var company = new Company
        {
            FullName = "Firma obca", ShortName = "Obca", Nip = "1234567802"
        };
        books.Db.Companies.Add(company);
        var foreign = new Counterparty
        {
            CompanyId = company.Id, Code = "EXT", Name = "Kontrahent obcy"
        };
        books.Db.Counterparties.Add(foreign);
        await books.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => stock.RegisterAsync(
            StockMovementType.PZ, item.Id, new DateOnly(2026, 1, 10), 10m, 20m,
            counterpartyId: foreign.Id, offsetAccount: null));
        Assert.Contains("aktualnej jednostki", error.Message);

        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.StockMovements.AnyAsync(m => m.StockItemId == item.Id));
        Assert.Equal(0m, (await books.Db.StockItems.SingleAsync(i => i.Id == item.Id)).Quantity);
    }

    [Fact]
    public async Task Inactive_stock_item_cannot_receive_or_issue_goods()
    {
        await using var books = await TestBooks.CreateAsync();
        var stock = new StockService(books.Db, books.User, books.Clock);
        var item = await stock.AddItemAsync(new StockItem
        {
            Code = "REG-3", Name = "Pozycja zablokowana", Kind = StockItemKind.Towar
        });
        item.IsActive = false;
        await books.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => stock.RegisterAsync(
            StockMovementType.PZ, item.Id, new DateOnly(2026, 1, 10), 1m, 20m));
        Assert.Contains("nieaktywna", error.Message);
        Assert.False(await books.Db.StockMovements.AnyAsync(m => m.StockItemId == item.Id));
    }
}
