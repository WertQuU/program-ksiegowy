using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Warehouse;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Warehouse;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class StockTests
{
    [Fact]
    public async Task Average_cost_issue_posting_and_inventory_differences()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new StockService(t.Db, t.User, t.Clock);
        var item = await svc.AddItemAsync(new StockItem { Code = "T1", Name = "Kubek", Kind = StockItemKind.Towar });
        await Assert.ThrowsAsync<DomainException>(() => svc.AddItemAsync(new StockItem { Code = "T1", Name = "Duplikat" }));

        await svc.RegisterAsync(StockMovementType.PZ, item.Id, new DateOnly(2026, 1, 5), 10m, 10m, "FV/1", offsetAccount: "300");
        await svc.RegisterAsync(StockMovementType.PZ, item.Id, new DateOnly(2026, 1, 6), 10m, 13m, "FV/2");
        await Assert.ThrowsAsync<DomainException>(() => svc.RegisterAsync(StockMovementType.WZ, item.Id, new DateOnly(2026, 1, 7), 25m));
        var wz = await svc.RegisterAsync(StockMovementType.WZ, item.Id, new DateOnly(2026, 1, 7), 5m, reference: "FS/1");
        Assert.Equal(57.50m, wz.Value); // średnia (100 + 130) / 20 = 11,50
        Assert.NotNull(wz.JournalEntryId);
        var minus = await svc.RegisterAsync(StockMovementType.InwNiedobor, item.Id, new DateOnly(2026, 1, 31), 1m);
        Assert.Equal(11.50m, minus.Value);
        await Assert.ThrowsAsync<DomainException>(() => svc.RegisterAsync(StockMovementType.WZ, item.Id, new DateOnly(2026, 1, 20), 1m));

        Assert.Equal(57.50m, await t.BalanceAsync("731"));
        Assert.Equal(11.50m, await t.BalanceAsync("761-03"));
        Assert.Equal(100m - 57.50m - 11.50m, await t.BalanceAsync("330")); // PZ 2 bez zapisu (faktura księgowana osobno)

        var state = await svc.StateAsync(new DateOnly(2026, 1, 31));
        Assert.Equal(14m, state.Single().Quantity);
        Assert.Equal(161m, state.Single().Value);
        var rs = new ReportService(t.Db, t.User, t.Clock);
        var sheet = await rs.StockStateAsync(new DateOnly(2026, 1, 31), true);
        Assert.Single(sheet.Rows);
        var st = await rs.StockStateAsync(new DateOnly(2026, 1, 6), false);
        Assert.Equal(230m, st.Rows.Single(r => r.IsTotal).Cells[6]);
    }
}
