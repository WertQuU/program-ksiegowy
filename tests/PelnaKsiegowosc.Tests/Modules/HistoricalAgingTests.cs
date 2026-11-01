using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Raport rozrachunków na dawny dzień nie może używać dzisiejszego statusu spłaty.</summary>
public sealed class HistoricalAgingTests
{
    [Fact]
    public async Task Fully_paid_receivable_is_still_outstanding_on_date_before_payment()
    {
        await using var books = await TestBooks.CreateAsync();
        var cp = await books.AddCounterpartyAsync("AGING01", "Klient historyczny");
        var ledger = books.Ledger();
        await ledger.CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 10), "FS/AGING01",
            new("200", 100m, 0m, cp.Id), new("700-01", 0m, 100m)));
        await books.Db.SaveChangesAsync();
        var original = await books.Db.OpenItems.SingleAsync();

        var aging = new SettlementService(books.Db, books.User, books.Clock);
        Assert.Single(await aging.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 1, 15)));
        Assert.Empty(await aging.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 1, 9)));

        await ledger.CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 20), "WB/AGING01",
            new("130-01", 100m, 0m), new("200", 0m, 100m, cp.Id, OpenItemId: original.Id)));
        await books.Db.SaveChangesAsync();

        var historical = await aging.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 1, 15));
        Assert.Single(historical);
        Assert.Equal(100m, historical[0].RemainingPln);
        Assert.Equal(100m, historical[0].Remaining);
        Assert.Empty(await aging.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 1, 21)));
    }

    [Fact]
    public async Task Partially_paid_receivable_uses_only_allocations_before_report_date()
    {
        await using var books = await TestBooks.CreateAsync();
        var cp = await books.AddCounterpartyAsync("AGING02", "Klient częściowych zapłat");
        var ledger = books.Ledger();
        await ledger.CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 10), "FS/AGING02",
            new("200", 200m, 0m, cp.Id), new("700-01", 0m, 200m)));
        await books.Db.SaveChangesAsync();
        var openItem = await books.Db.OpenItems.SingleAsync();

        await ledger.CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 20), "WB/AGING02A",
            new("130-01", 50m, 0m), new("200", 0m, 50m, cp.Id, OpenItemId: openItem.Id)));
        await ledger.CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 2, 10), "WB/AGING02B",
            new("130-01", 150m, 0m), new("200", 0m, 150m, cp.Id, OpenItemId: openItem.Id)));
        await books.Db.SaveChangesAsync();

        var aging = new SettlementService(books.Db, books.User, books.Clock);
        var beforePayments = await aging.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 1, 15));
        Assert.Equal(200m, Assert.Single(beforePayments).RemainingPln);
        var afterFirst = await aging.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 1, 31));
        Assert.Equal(150m, Assert.Single(afterFirst).RemainingPln);
        Assert.Empty(await aging.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 2, 11)));
    }
}
