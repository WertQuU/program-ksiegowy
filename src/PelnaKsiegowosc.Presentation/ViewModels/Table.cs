using System.Globalization;
using System.Text;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Presentation.ViewModels;

public sealed record TableColumn(string Header, ColumnKind Kind = ColumnKind.Text, double Width = 1.0)
{
    public bool AlignRight => Kind is ColumnKind.Money or ColumnKind.Integer or ColumnKind.Decimal;
}

/// <summary>Wiersz tabeli — komórki sformatowane po polsku; <see cref="Tag"/> wskazuje obiekt źródłowy (np. identyfikator dokumentu).</summary>
public sealed class TableRow
{
    public TableRow(object? tag, string[] cells)
    {
        Tag = tag;
        Cells = cells;
    }

    public object? Tag { get; }
    public string[] Cells { get; }
    public bool IsTotal { get; init; }
    public bool IsHeader { get; init; }
    public int Level { get; init; }

    public Guid Id => Tag is Guid g ? g : throw new InvalidOperationException("Wiersz nie wskazuje obiektu.");
}

/// <summary>Dane tabeli wyświetlanej w ekranach (lista dokumentów, raport, rejestr).</summary>
public sealed class TableData
{
    public TableData(params TableColumn[] columns) => Columns = columns.ToList();

    public List<TableColumn> Columns { get; }
    public List<TableRow> Rows { get; } = new();
    public List<string> Notes { get; } = new();

    public static readonly TableData Empty = new();

    public TableRow Add(object? tag, params object?[] values)
    {
        var row = new TableRow(tag, values.Select((v, i) => Display.Cell(v, i < Columns.Count ? Columns[i].Kind : ColumnKind.Text)).ToArray());
        Rows.Add(row);
        return row;
    }

    public void AddRow(TableRow row) => Rows.Add(row);

    public static TableData FromReport(ReportDocument r)
    {
        var t = new TableData(r.Columns.Select(c => new TableColumn(c.Header, c.Kind, c.Width)).ToArray());
        foreach (var row in r.Rows)
            t.Rows.Add(new TableRow(null, row.Cells.Select((v, i) => Display.Cell(v, i < r.Columns.Count ? r.Columns[i].Kind : ColumnKind.Text)).ToArray())
                { IsTotal = row.IsTotal, IsHeader = row.IsHeader, Level = row.Level });
        t.Notes.AddRange(r.Notes);
        if (!r.ControlsPassed) t.Notes.Insert(0, "UWAGA: kontrole raportu nie zostały spełnione.");
        return t;
    }
}

/// <summary>Formatowanie wartości do wyświetlenia (kwoty i daty w formacie polskim, wyliczenia czytelnie).</summary>
public static class Display
{
    public static string Cell(object? v, ColumnKind kind) => v switch
    {
        null => "",
        string s => s,
        decimal d when kind is ColumnKind.Money => Money.Format(d),
        decimal d => d.ToString("0.####", Money.Pl),
        int or long when kind is ColumnKind.Money => Money.Format(Convert.ToDecimal(v, CultureInfo.InvariantCulture)),
        DateOnly d => d.ToPl(),
        DateTime dt => dt.ToPlDateTime(),
        bool b => b ? "tak" : "nie",
        Enum e => Enum(e),
        IFormattable f => f.ToString(null, Money.Pl),
        _ => v.ToString() ?? ""
    };

    /// <summary>„CzynnyPodatnikVat” → „Czynny podatnik vat”; „Z4_KosztyRodzajowe” → „Z4 koszty rodzajowe”.</summary>
    public static string Enum(Enum e)
    {
        var s = e.ToString().Replace('_', ' ');
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && s[i - 1] != ' ' && !char.IsUpper(s[i - 1])) sb.Append(' ').Append(char.ToLower(c, Money.Pl));
            else if (i > 0 && char.IsUpper(c) && s[i - 1] == ' ') sb.Append(char.ToLower(c, Money.Pl));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);
}

/// <summary>Pozycja listy wyboru (wyliczenie, konto, kontrahent itp.).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public static class Choices
{
    public static List<Choice<T>> Of<T>() where T : struct, Enum =>
        System.Enum.GetValues<T>().Select(v => new Choice<T>(v, Display.Enum(v))).ToList();
}

/// <summary>Parsowanie pól formularzy (kwoty, daty) z czytelnym komunikatem.</summary>
public static class Parse
{
    public static decimal Amount(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        return Money.TryParse(text, out var v) ? v : throw new DomainException($"Pole „{field}”: nieprawidłowa kwota „{text}”.");
    }

    public static decimal? OptionalAmount(string? text, string field) =>
        string.IsNullOrWhiteSpace(text) ? null : Amount(text, field);

    public static DateOnly Date(string? text, string field) =>
        DateFormat.TryParsePl(text, out var d) ? d : throw new DomainException($"Pole „{field}”: podaj datę w formacie dd.mm.rrrr.");

    public static DateOnly? OptionalDate(string? text, string field) =>
        string.IsNullOrWhiteSpace(text) ? null : Date(text, field);

    public static int Int(string? text, string field) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new DomainException($"Pole „{field}”: podaj liczbę całkowitą.");

    /// <summary>Okres VAT w formacie mm.rrrr lub rrrr-mm → pierwszy dzień miesiąca.</summary>
    public static DateOnly Month(string? text, string field)
    {
        var t = text?.Trim() ?? "";
        foreach (var f in new[] { "MM.yyyy", "M.yyyy", "yyyy-MM", "MM/yyyy" })
            if (DateOnly.TryParseExact(t, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return new DateOnly(d.Year, d.Month, 1);
        throw new DomainException($"Pole „{field}”: podaj miesiąc w formacie mm.rrrr.");
    }

    public static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
