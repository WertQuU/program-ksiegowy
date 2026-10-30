using PelnaKsiegowosc.Application.Warehouse;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Application.Reports;

public sealed partial class ReportService
{
    /// <summary>Stan magazynu na dzień; <paramref name="countSheet"/> — arkusz spisu z natury z pustą kolumną ilości stwierdzonej.</summary>
    public async Task<ReportDocument> StockStateAsync(DateOnly asOf, bool countSheet, CancellationToken ct = default)
    {
        var state = await new StockService(_db, _user, _clock).StateAsync(asOf, ct);
        var r = await NewAsync(countSheet ? "Arkusz spisu z natury" : "Stan magazynu", $"na dzień {asOf.ToPl()}", true, ct);
        r.Columns.AddRange(countSheet
            ? new[] { new ReportColumn("Lp.", ColumnKind.Integer, 0.4), new ReportColumn("Kod"), new ReportColumn("Nazwa", ColumnKind.Text, 2.2), new ReportColumn("J.m."),
                new ReportColumn("Ilość stwierdzona"), new ReportColumn("Cena", ColumnKind.Decimal), new ReportColumn("Wartość"), new ReportColumn("Ilość wg ksiąg", ColumnKind.Decimal) }
            : new[] { new ReportColumn("Kod"), new ReportColumn("Nazwa", ColumnKind.Text, 2.2), new ReportColumn("J.m."), new ReportColumn("Konto"),
                new ReportColumn("Ilość", ColumnKind.Decimal), new ReportColumn("Cena przeciętna", ColumnKind.Decimal), new ReportColumn("Wartość", ColumnKind.Money) });
        var lp = 1;
        foreach (var (item, qty, value) in state.Where(s => countSheet ? s.Item.IsActive : s.Quantity != 0 || s.Value != 0))
        {
            var price = qty == 0 ? 0 : Math.Round(value / qty, 4);
            if (countSheet) r.Add(lp++, item.Code, item.Name, item.Unit, "", price, "", qty);
            else r.Add(item.Code, item.Name, item.Unit, item.InventoryAccount, qty, price, value);
        }
        if (!countSheet)
        {
            r.AddRow(new ReportRow("RAZEM", null, null, null, null, null, state.Sum(s => s.Value)) { IsTotal = true });
            foreach (var g in state.GroupBy(s => s.Item.InventoryAccount))
                r.Notes.Add($"Konto {g.Key}: wartość magazynu {Money.Format(g.Sum(s => s.Value))} zł — porównaj z saldem konta w zestawieniu obrotów i sald.");
        }
        else r.Notes.Add("Spis: komisja wpisuje ilość stwierdzoną; różnice ujmij w sekcji Magazyn jako „Niedobór” / „Nadwyżka” z datą spisu. Podpisy komisji i osoby materialnie odpowiedzialnej: ……………");
        return r;
    }
}
