namespace PelnaKsiegowosc.Application.Reports;

public enum ColumnKind
{
    Text,
    Money,
    Date,
    Integer,
    Decimal
}

public sealed record ReportColumn(string Header, ColumnKind Kind = ColumnKind.Text, double Width = 1.0);

public sealed class ReportRow
{
    public ReportRow(params object?[] cells) => Cells = cells;
    public object?[] Cells { get; }
    public int Level { get; init; }
    public bool IsTotal { get; init; }
    public bool IsHeader { get; init; }
}

/// <summary>
/// Raport niezależny od formatu wyjściowego (PDF/XLSX/CSV). Każdy raport wskazuje okres, datę wygenerowania,
/// zastosowane filtry i walutę. Dane pochodzą wyłącznie z zapisów księgowych i ewidencji.
/// </summary>
public sealed class ReportDocument
{
    public required string Title { get; init; }
    public required string CompanyName { get; init; }
    public string? CompanyNip { get; init; }
    public required string PeriodDescription { get; init; }
    public DateTime GeneratedAtUtc { get; init; }
    public string GeneratedBy { get; init; } = "";
    public string Currency { get; init; } = "PLN";
    public List<(string Name, string Value)> Filters { get; } = new();
    public List<ReportColumn> Columns { get; } = new();
    public List<ReportRow> Rows { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Landscape { get; init; }
    /// <summary>Wynik kontroli (np. bilans: aktywa = pasywa). Fałsz = raport z ostrzeżeniem.</summary>
    public bool ControlsPassed { get; set; } = true;

    public ReportRow Add(params object?[] cells)
    {
        var r = new ReportRow(cells);
        Rows.Add(r);
        return r;
    }

    public void AddRow(ReportRow row) => Rows.Add(row);
}

/// <summary>Eksporter raportu do formatu pliku.</summary>
public interface IReportExporter
{
    string Format { get; }
    string FileExtension { get; }
    byte[] Export(ReportDocument report);
}
