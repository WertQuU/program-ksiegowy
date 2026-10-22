using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Infrastructure.Export;

internal static class ReportFormatting
{
    public static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static string Cell(object? v, ColumnKind kind) => v switch
    {
        null => "",
        decimal d when kind is ColumnKind.Money => Money.Format(d),
        decimal d => d.ToString("0.####", Pl),
        DateOnly d => d.ToPl(),
        DateTime dt => dt.ToPlDateTime(),
        long or int => Convert.ToString(v, Pl) ?? "",
        _ => v.ToString() ?? ""
    };

    public static IEnumerable<string> HeaderLines(ReportDocument r)
    {
        yield return r.CompanyName + (r.CompanyNip is null ? "" : $" (NIP {r.CompanyNip})");
        yield return r.Title;
        yield return $"Okres: {r.PeriodDescription}";
        yield return $"Waluta: {r.Currency}; wygenerowano: {r.GeneratedAtUtc.ToPlDateTime()} (Europe/Warsaw); użytkownik: {r.GeneratedBy}";
        if (r.Filters.Count > 0) yield return "Filtry: " + string.Join("; ", r.Filters.Select(f => $"{f.Name}: {f.Value}"));
    }
}

/// <summary>CSV: UTF-8 z BOM, separator „;”, liczby w zapisie polskim (przecinek dziesiętny, bez separatora tysięcy).</summary>
public sealed class CsvReportExporter : IReportExporter
{
    public string Format => "CSV";
    public string FileExtension => ".csv";

    public byte[] Export(ReportDocument r)
    {
        var sb = new StringBuilder();
        foreach (var h in ReportFormatting.HeaderLines(r)) sb.AppendLine(Esc(h));
        sb.AppendLine();
        sb.AppendLine(string.Join(';', r.Columns.Select(c => Esc(c.Header))));
        foreach (var row in r.Rows)
        {
            var cells = r.Columns.Select((c, i) =>
            {
                var v = i < row.Cells.Length ? row.Cells[i] : null;
                return v switch
                {
                    null => "",
                    decimal d => d.ToString("0.00", ReportFormatting.Pl),
                    _ => Esc(ReportFormatting.Cell(v, c.Kind))
                };
            });
            sb.AppendLine(string.Join(';', cells));
        }
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in r.Notes) sb.AppendLine(Esc("Uwaga: " + n));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Esc(string s)
    {
        // ochrona przed wstrzyknięciem formuł w arkuszach (CSV injection)
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Any, ReportFormatting.Pl, out _)) s = "'" + s;
        return s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>XLSX (ClosedXML, licencja MIT): nagłówek raportu, tabela z formatami liczbowymi, uwagi.</summary>
public sealed class XlsxReportExporter : IReportExporter
{
    public string Format => "XLSX";
    public string FileExtension => ".xlsx";

    public byte[] Export(ReportDocument r)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName(r.Title));
        var row = 1;
        foreach (var h in ReportFormatting.HeaderLines(r))
        {
            ws.Cell(row, 1).Value = h;
            if (row == 2) ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(14);
            row++;
        }
        row++;
        var headerRow = row;
        for (var c = 0; c < r.Columns.Count; c++)
        {
            var cell = ws.Cell(row, c + 1);
            cell.Value = r.Columns[c].Header;
            cell.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray).Alignment.SetWrapText(true);
        }
        row++;
        foreach (var rr in r.Rows)
        {
            for (var c = 0; c < r.Columns.Count; c++)
            {
                var v = c < rr.Cells.Length ? rr.Cells[c] : null;
                var cell = ws.Cell(row, c + 1);
                switch (v)
                {
                    case null: break;
                    case decimal d:
                        cell.Value = d;
                        cell.Style.NumberFormat.Format = r.Columns[c].Kind == ColumnKind.Money ? "#,##0.00" : "0.####";
                        break;
                    case long l: cell.Value = l; break;
                    case int i: cell.Value = i; break;
                    case DateOnly dt:
                        cell.Value = dt.ToDateTime(TimeOnly.MinValue);
                        cell.Style.DateFormat.Format = "dd.mm.yyyy";
                        break;
                    default:
                        var s = v.ToString() ?? "";
                        cell.SetValue(s); // zawsze jako tekst — bez interpretacji formuł
                        break;
                }
                if (rr.IsTotal || rr.IsHeader) cell.Style.Font.SetBold();
                if (c == 1 && rr.Level > 0) cell.Style.Alignment.SetIndent(rr.Level);
            }
            row++;
        }
        ws.Range(headerRow, 1, row - 1, r.Columns.Count).SetAutoFilter();
        ws.SheetView.FreezeRows(headerRow);
        for (var c = 0; c < r.Columns.Count; c++) ws.Column(c + 1).Width = Math.Clamp(12 * r.Columns[c].Width, 8, 70);
        if (r.Notes.Count > 0)
        {
            row++;
            foreach (var n in r.Notes) ws.Cell(row++, 1).SetValue("Uwaga: " + n);
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static string SheetName(string title)
    {
        var s = new string(title.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        return s.Length > 31 ? s[..31] : s;
    }
}

/// <summary>
/// PDF (PDFsharp + MigraDoc, licencja MIT). Czcionka z polskimi znakami wyszukiwana w systemie
/// (Windows: Arial/Segoe UI; Linux: Liberation/DejaVu) — wbudowywana w dokument.
/// </summary>
public sealed class PdfReportExporter : IReportExporter
{
    public string Format => "PDF";
    public string FileExtension => ".pdf";

    static PdfReportExporter() => SystemFontResolver.EnsureRegistered();

    public byte[] Export(ReportDocument r)
    {
        var doc = new MigraDoc.DocumentObjectModel.Document();
        doc.Info.Title = r.Title;
        doc.Info.Author = r.CompanyName;
        var style = doc.Styles["Normal"]!;
        style.Font.Name = SystemFontResolver.FamilyName;
        style.Font.Size = r.Columns.Count > 9 ? 6.5 : 8;
        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = r.Landscape ? Orientation.Landscape : Orientation.Portrait;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.2);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);

        var header = section.Headers.Primary.AddParagraph($"{r.CompanyName} — {r.Title}");
        header.Format.Font.Size = 7;
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.AddText($"Wygenerowano {r.GeneratedAtUtc.ToPlDateTime()} • Pełna Księgowość • strona ");
        footer.AddPageField();
        footer.AddText(" z ");
        footer.AddNumPagesField();

        var lines = ReportFormatting.HeaderLines(r).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var p = section.AddParagraph(lines[i]);
            if (i == 1) { p.Format.Font.Size = 13; p.Format.Font.Bold = true; p.Format.SpaceBefore = 4; p.Format.SpaceAfter = 4; }
        }
        section.AddParagraph().Format.SpaceAfter = 4;

        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = Colors.Gray;
        var pageWidth = (r.Landscape ? 29.7 : 21.0) - 2.4;
        var totalWeight = r.Columns.Sum(c => c.Width);
        foreach (var c in r.Columns)
        {
            var col = table.AddColumn(Unit.FromCentimeter(pageWidth * c.Width / totalWeight));
            col.Format.Alignment = c.Kind is ColumnKind.Money or ColumnKind.Integer or ColumnKind.Decimal ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }
        var hr = table.AddRow();
        hr.HeadingFormat = true;
        hr.Shading.Color = Colors.LightGray;
        hr.Format.Font.Bold = true;
        for (var i = 0; i < r.Columns.Count; i++) hr.Cells[i].AddParagraph(r.Columns[i].Header);
        foreach (var rr in r.Rows)
        {
            var tr = table.AddRow();
            if (rr.IsTotal || rr.IsHeader) tr.Format.Font.Bold = true;
            if (rr.IsTotal) tr.Shading.Color = new Color(245, 245, 245);
            for (var i = 0; i < r.Columns.Count; i++)
            {
                var text = ReportFormatting.Cell(i < rr.Cells.Length ? rr.Cells[i] : null, r.Columns[i].Kind);
                var p = tr.Cells[i].AddParagraph(text);
                if (i == 1 && rr.Level > 0) p.Format.LeftIndent = Unit.FromMillimeter(2 * rr.Level);
            }
        }
        if (r.Notes.Count > 0)
        {
            section.AddParagraph().Format.SpaceAfter = 6;
            foreach (var n in r.Notes) section.AddParagraph("Uwaga: " + n).Format.Font.Italic = true;
        }
        if (!r.ControlsPassed)
        {
            var w = section.AddParagraph("UWAGA: kontrole raportu wykazały niezgodności — patrz uwagi powyżej.");
            w.Format.Font.Bold = true;
            w.Format.Font.Color = Colors.DarkRed;
        }
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }
}

/// <summary>Resolver czcionek dla PDFsharp (build „Core” — niezależny od GDI/WPF), wyszukujący pliki TTF w katalogach systemowych.</summary>
public sealed class SystemFontResolver : IFontResolver
{
    public const string FamilyName = "PsaSans";
    private static readonly object Lock = new();
    private static bool _registered;
    private readonly Dictionary<string, string> _files;

    private SystemFontResolver(Dictionary<string, string> files) => _files = files;

    public static void EnsureRegistered()
    {
        lock (Lock)
        {
            if (_registered) return;
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new SystemFontResolver(FindFonts());
            _registered = true;
        }
    }

    private static Dictionary<string, string> FindFonts()
    {
        var dirs = new List<string>();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(win)) dirs.Add(win);
        dirs.AddRange(new[] { "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts" });
        var candidates = new (string regular, string bold, string italic, string boldItalic)[]
        {
            ("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"),
            ("segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"),
            ("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf", "LiberationSans-BoldItalic.ttf"),
            ("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf")
        };
        var all = dirs.Where(Directory.Exists)
            .SelectMany(d => { try { return Directory.EnumerateFiles(d, "*.ttf", SearchOption.AllDirectories); } catch (UnauthorizedAccessException) { return Array.Empty<string>(); } })
            .GroupBy(f => Path.GetFileName(f).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in candidates)
        {
            if (!all.TryGetValue(c.regular.ToLowerInvariant(), out var reg)) continue;
            string Pick(string n) => all.TryGetValue(n.ToLowerInvariant(), out var f) ? f : reg;
            return new Dictionary<string, string>
            {
                ["R"] = reg, ["B"] = Pick(c.bold), ["I"] = Pick(c.italic), ["BI"] = Pick(c.boldItalic)
            };
        }
        throw new InvalidOperationException("Nie znaleziono czcionki TrueType z polskimi znakami (Arial, Segoe UI, Liberation Sans lub DejaVu Sans).");
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new((bold, italic) switch { (true, true) => "BI", (true, false) => "B", (false, true) => "I", _ => "R" });

    public byte[]? GetFont(string faceName) => File.ReadAllBytes(_files[faceName]);
}
