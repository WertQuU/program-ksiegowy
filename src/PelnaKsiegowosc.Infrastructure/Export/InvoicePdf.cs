using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Vat;
using MDocument = MigraDoc.DocumentObjectModel.Document;
using Document = PelnaKsiegowosc.Domain.Documents.Document;

namespace PelnaKsiegowosc.Infrastructure.Export;

/// <summary>
/// Wydruk faktury sprzedaży (PDF) z elementami art. 106e ustawy o VAT obsługiwanymi przez program. Dla faktury
/// wystawionej w KSeF fakturą jest dokument ustrukturyzowany — wydruk jest jego wizualizacją z numerem KSeF.
/// </summary>
public static class InvoicePdf
{
    static InvoicePdf() => SystemFontResolver.EnsureRegistered();

    public static byte[] Build(Document d, Company seller, Document? corrected = null)
    {
        if (!d.IsSales) throw new DomainException("Wydruk dotyczy faktur sprzedaży.");
        var doc = new MDocument();
        doc.Info.Title = $"Faktura {d.Number}";
        doc.Info.Author = seller.FullName;
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = SystemFontResolver.FamilyName;
        normal.Font.Size = 9;
        var s = doc.AddSection();
        s.PageSetup = doc.DefaultPageSetup.Clone();
        s.PageSetup.PageFormat = PageFormat.A4;
        s.PageSetup.LeftMargin = s.PageSetup.RightMargin = Unit.FromCentimeter(1.6);
        s.PageSetup.TopMargin = Unit.FromCentimeter(1.4);

        var title = d.IsCorrection ? "Faktura korygująca" : seller.VatStatus == VatStatus.CzynnyPodatnikVat ? "Faktura" : "Faktura (sprzedaż zwolniona z VAT)";
        var t = s.AddParagraph($"{title} nr {d.Number}");
        t.Format.Font.Size = 16;
        t.Format.Font.Bold = true;
        t.Format.SpaceAfter = 2;
        if (!string.IsNullOrEmpty(d.KsefNumber))
            s.AddParagraph($"Numer KSeF: {d.KsefNumber}").Format.Font.Bold = true;
        var dates = s.AddParagraph($"Data wystawienia: {d.IssueDate.ToPl()}    Data sprzedaży: {d.OperationDate.ToPl()}" +
                                   (d.DueDate is { } due ? $"    Termin płatności: {due.ToPl()}" : ""));
        dates.Format.SpaceAfter = 10;
        if (d.IsCorrection && corrected is not null)
        {
            var c = s.AddParagraph($"Dotyczy faktury nr {corrected.Number} z dnia {corrected.IssueDate.ToPl()}" +
                                   (corrected.KsefNumber is { } k ? $" (KSeF {k})" : "") + $". Przyczyna korekty: {d.CorrectionReason}");
            c.Format.SpaceAfter = 8;
        }

        var parties = s.AddTable();
        parties.AddColumn(Unit.FromCentimeter(8.9));
        parties.AddColumn(Unit.FromCentimeter(8.9));
        var pr = parties.AddRow();
        void Party(Cell cell, string header, string name, string? nip, string? address)
        {
            var h = cell.AddParagraph(header);
            h.Format.Font.Bold = true;
            h.Format.Font.Color = Colors.Gray;
            cell.AddParagraph(name).Format.Font.Bold = true;
            if (!string.IsNullOrWhiteSpace(address)) cell.AddParagraph(address);
            if (!string.IsNullOrWhiteSpace(nip)) cell.AddParagraph($"NIP: {nip}");
        }
        Party(pr.Cells[0], "Sprzedawca", seller.FullName, seller.Nip, $"{seller.AddressLine1}, {seller.AddressLine2}");
        Party(pr.Cells[1], "Nabywca", d.CounterpartyName ?? "", d.CounterpartyNip, d.CounterpartyAddress);
        s.AddParagraph().Format.SpaceAfter = 8;

        var table = s.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = Colors.Gray;
        foreach (var (w, right) in new[] { (0.8, false), (5.4, false), (1.1, false), (1.3, true), (2.0, true), (2.2, true), (1.2, false), (1.6, true), (2.2, true) })
            table.AddColumn(Unit.FromCentimeter(w)).Format.Alignment = right ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = new Color(235, 240, 247);
        head.Format.Font.Bold = true;
        var heads = new[] { "Lp.", "Nazwa towaru lub usługi", "J.m.", "Ilość", "Cena netto", "Wartość netto", "VAT", "Kwota VAT", "Brutto" };
        for (var i = 0; i < heads.Length; i++) head.Cells[i].AddParagraph(heads[i]);
        foreach (var l in d.Lines.OrderBy(l => l.LineNo))
        {
            var r = table.AddRow();
            var cells = new[] { l.LineNo.ToString(), l.Name, l.Unit, l.Quantity.ToString("0.####", Money.Pl), Money.Format(l.UnitNetPrice), Money.Format(l.NetAmount),
                RateLabel(l.VatRateCode, d.IssueDate), Money.Format(l.VatAmount), Money.Format(l.GrossAmount) };
            for (var i = 0; i < cells.Length; i++) r.Cells[i].AddParagraph(cells[i]);
        }
        s.AddParagraph().Format.SpaceAfter = 6;

        var sum = s.AddTable();
        sum.Borders.Width = 0.25;
        sum.Borders.Color = Colors.Gray;
        sum.Rows.LeftIndent = Unit.FromCentimeter(8.4);
        foreach (var w in new[] { 2.4, 2.6, 2.2, 2.4 }) sum.AddColumn(Unit.FromCentimeter(w)).Format.Alignment = ParagraphAlignment.Right;
        var sh = sum.AddRow();
        sh.Format.Font.Bold = true;
        sh.Shading.Color = new Color(235, 240, 247);
        foreach (var (h, i) in new[] { "Stawka", "Netto", "VAT", "Brutto" }.Select((h, i) => (h, i))) sh.Cells[i].AddParagraph(h);
        foreach (var v in d.VatLines.OrderBy(v => v.VatRateCode))
        {
            var r = sum.AddRow();
            r.Cells[0].AddParagraph(RateLabel(v.VatRateCode, d.IssueDate));
            r.Cells[1].AddParagraph(Money.Format(v.NetAmount));
            r.Cells[2].AddParagraph(Money.Format(v.VatAmount));
            r.Cells[3].AddParagraph(Money.Format(v.NetAmount + v.VatAmount));
        }
        var tr = sum.AddRow();
        tr.Format.Font.Bold = true;
        tr.Cells[0].AddParagraph("Razem");
        tr.Cells[1].AddParagraph(Money.Format(d.NetAmount));
        tr.Cells[2].AddParagraph(Money.Format(d.VatAmount));
        tr.Cells[3].AddParagraph(Money.Format(d.GrossAmount));

        var pay = s.AddParagraph($"{(d.GrossAmount < 0 ? "Do zwrotu" : "Do zapłaty")}: {Money.Format(Math.Abs(d.GrossAmount))} {d.Currency}");
        pay.Format.Font.Size = 13;
        pay.Format.Font.Bold = true;
        pay.Format.SpaceBefore = 10;
        if (d.Currency != "PLN")
            s.AddParagraph($"Kurs {d.Currency}: {d.ExchangeRate:0.0000} (tabela NBP {d.ExchangeRateTable}, z dnia {d.ExchangeRateDate.ToPl()}). " +
                           $"Kwota VAT w złotych: {Money.Format(d.VatAmountPln)} zł.");
        s.AddParagraph($"Sposób płatności: {PaymentName(d.PaymentMethod)}" + (string.IsNullOrWhiteSpace(d.BankAccountIban) ? "" : $"    Rachunek: {FormatIban(d.BankAccountIban)}"));
        if (d.SplitPayment) s.AddParagraph("mechanizm podzielonej płatności").Format.Font.Bold = true;
        if (d.ReverseCharge) s.AddParagraph("odwrotne obciążenie").Format.Font.Bold = true;
        if (d.Lines.Any(l => l.VatRateCode == "zw"))
            s.AddParagraph("Podstawa zwolnienia z VAT: " + (d.VatExemptionBasis ?? (seller.VatStatus == VatStatus.ZwolnionyPodmiotowo ? "art. 113 ust. 1 ustawy o VAT" : "[wskaż przepis]")));
        if (!string.IsNullOrWhiteSpace(d.Description)) s.AddParagraph("Uwagi: " + d.Description).Format.SpaceBefore = 6;
        if (!string.IsNullOrWhiteSpace(seller.InvoiceFooter)) s.AddParagraph(seller.InvoiceFooter).Format.SpaceBefore = 10;
        if (!string.IsNullOrEmpty(d.KsefNumber))
        {
            var k = s.AddParagraph("Faktura wystawiona w Krajowym Systemie e-Faktur — dokument jest wizualizacją faktury ustrukturyzowanej.");
            k.Format.SpaceBefore = 10;
            k.Format.Font.Size = 7.5;
            k.Format.Font.Color = Colors.Gray;
        }
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static string RateLabel(string code, DateOnly date) =>
        VatRules.TryGet(code, date, out var r) ? (r.Percent is { } p ? $"{p:0}%" : r.Code) : code;

    private static string PaymentName(PaymentMethod m) => m switch
    {
        PaymentMethod.Przelew => "przelew",
        PaymentMethod.Gotowka => "gotówka",
        PaymentMethod.Karta => "karta",
        PaymentMethod.Kompensata => "kompensata",
        _ => "inna"
    };

    private static string FormatIban(string iban)
    {
        var x = iban.Replace(" ", "");
        return string.Join(" ", Enumerable.Range(0, (x.Length + 3) / 4).Select(i => x.Substring(i * 4, Math.Min(4, x.Length - i * 4))));
    }
}
