using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Warehouse;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Periods;
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

    [Fact]
    public async Task Unposted_receipt_cannot_change_stock_in_closed_accounting_period()
    {
        await using var books = await TestBooks.CreateAsync();
        var stock = new StockService(books.Db, books.User, books.Clock);
        var item = await stock.AddItemAsync(new StockItem
        {
            Code = "REG-4", Name = "Towar bez dekretu PZ", Kind = StockItemKind.Towar
        });
        var postingDate = new DateOnly(2026, 1, 10);
        var period = books.Year.Periods.Single(p => p.Contains(postingDate));
        period.Status = PeriodStatus.Zamkniety;
        await books.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            stock.RegisterAsync(StockMovementType.PZ, item.Id, postingDate,
                5m, 100m, offsetAccount: null));
        Assert.Contains("zamkniętym okresie", error.Message);

        await books.AddCounterpartyAsync("POSTCLOSED", "Zapis niezwiązany z magazynem");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.StockMovements.AnyAsync(m => m.StockItemId == item.Id));
        Assert.Equal(0m, (await books.Db.StockItems.AsNoTracking()
            .SingleAsync(i => i.Id == item.Id)).Quantity);
    }

    [Fact]
    public async Task Unknown_stock_document_kind_is_rejected_before_mutation()
    {
        await using var books = await TestBooks.CreateAsync();
        var stock = new StockService(books.Db, books.User, books.Clock);
        var item = await stock.AddItemAsync(new StockItem
        {
            Code = "REG-5", Name = "Towar", Kind = StockItemKind.Towar
        });

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            stock.RegisterAsync((StockMovementType)999, item.Id,
                new DateOnly(2026, 1, 10), 1m, 10m));
        Assert.Contains("Nieznany rodzaj", error.Message);
        Assert.False(await books.Db.StockMovements.AnyAsync(m => m.StockItemId == item.Id));
    }

    [Fact]
    public async Task Fifo_rejects_issue_when_inventory_differs_from_historical_lots()
    {
        await using var books = await TestBooks.CreateAsync();
        var stock = new StockService(books.Db, books.User, books.Clock);
        var item = await stock.AddItemAsync(new StockItem
        {
            Code = "REG-FIFO", Name = "Towar FIFO", Kind = StockItemKind.Towar,
            Valuation = StockValuation.Fifo
        });
        await stock.RegisterAsync(StockMovementType.PZ, item.Id,
            new DateOnly(2026, 1, 5), 10m, 5m);

        // Symulacja nieuzgodnionej ingerencji w bazę: kartoteka mówi 20 szt.,
        // ale historia FIFO posiada tylko 10 szt. Księgowanie nie może
        // wyliczyć zaniżonego kosztu wydania z niepełnych partii.
        item.Quantity = 20m;
        item.Value = 100m;
        await books.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            stock.RegisterAsync(StockMovementType.WZ, item.Id,
                new DateOnly(2026, 1, 6), 15m, reference: "FS/FIFO-CHECK"));
        Assert.Contains("Niespójna historia magazynowa FIFO", error.Message);

        await books.AddCounterpartyAsync("AFTERFIFO", "Po odmowie FIFO");
        books.Db.ChangeTracker.Clear();
        var stored = await books.Db.StockItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(20m, stored.Quantity);
        Assert.Equal(100m, stored.Value);
        Assert.Equal(1, await books.Db.StockMovements.CountAsync(m => m.StockItemId == item.Id));
    }
}
