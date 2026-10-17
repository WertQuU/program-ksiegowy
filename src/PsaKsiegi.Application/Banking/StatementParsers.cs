using System.Globalization;
using System.Text;
using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Application.Banking;

public sealed record ParsedTransaction(DateOnly BookingDate, DateOnly? ValueDate, decimal Amount, string Currency, string? CounterpartyName,
    string? CounterpartyIban, string Title, string? Reference);

public sealed record ParsedStatement(string? Number, string? AccountIban, DateOnly? DateFrom, DateOnly? DateTo, decimal? OpeningBalance,
    decimal? ClosingBalance, string Currency, IReadOnlyList<ParsedTransaction> Transactions);

/// <summary>
/// Profil importu CSV — mapowanie kolumn. Każdy bank stosuje inny układ; profil konfiguruje użytkownik.
/// Aplikacja NIE deklaruje obsługi API żadnego konkretnego banku.
/// </summary>
public sealed class CsvImportProfile
{
    public string Name { get; set; } = "CSV — ogólny";
    public char Separator { get; set; } = ';';
    public int HeaderRows { get; set; } = 1;
    public string Encoding { get; set; } = "utf-8";
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public int BookingDateColumn { get; set; }
    public int? ValueDateColumn { get; set; }
    public int AmountColumn { get; set; } = 1;
    /// <summary>Jeżeli ustawione — kolumny „uznania” i „obciążenia” zamiast jednej kwoty ze znakiem.</summary>
    public int? CreditColumn { get; set; }
    public int? DebitColumn { get; set; }
    public int? CurrencyColumn { get; set; }
    public int? CounterpartyNameColumn { get; set; } = 2;
    public int? CounterpartyIbanColumn { get; set; } = 3;
    public int TitleColumn { get; set; } = 4;
    public int? ReferenceColumn { get; set; } = 5;
    public string DefaultCurrency { get; set; } = "PLN";
}

public static class CsvStatementParser
{
    public static ParsedStatement Parse(byte[] content, CsvImportProfile p)
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc = System.Text.Encoding.GetEncoding(p.Encoding);
        var text = enc.GetString(content).TrimStart('﻿');
        var rows = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var list = new List<ParsedTransaction>();
        for (var i = p.HeaderRows; i < rows.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(rows[i])) continue;
            var f = Ledger.ChartService.SplitCsv(rows[i], p.Separator);
            string Col(int idx) => idx < f.Count ? f[idx].Trim() : throw new DomainException($"Wiersz {i + 1}: brak kolumny {idx + 1}.");
            if (!DateOnly.TryParseExact(Col(p.BookingDateColumn), p.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new DomainException($"Wiersz {i + 1}: nieprawidłowa data „{Col(p.BookingDateColumn)}” (oczekiwany format {p.DateFormat}).");
            DateOnly? valueDate = null;
            if (p.ValueDateColumn is { } vdc && DateOnly.TryParseExact(Col(vdc), p.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var vd)) valueDate = vd;
            decimal amount;
            if (p.CreditColumn is { } cc && p.DebitColumn is { } dc)
            {
                var credit = Money.TryParse(Col(cc), out var c) ? c : 0m;
                var debit = Money.TryParse(Col(dc), out var d) ? d : 0m;
                amount = Math.Abs(credit) - Math.Abs(debit);
            }
            else if (!Money.TryParse(Col(p.AmountColumn), out amount))
                throw new DomainException($"Wiersz {i + 1}: nieprawidłowa kwota „{Col(p.AmountColumn)}”.");
            if (amount == 0) continue;
            list.Add(new ParsedTransaction(date, valueDate, amount,
                p.CurrencyColumn is { } cur ? Col(cur).ToUpperInvariant() : p.DefaultCurrency,
                p.CounterpartyNameColumn is { } n ? NullIfEmpty(Col(n)) : null,
                p.CounterpartyIbanColumn is { } ib ? NullIfEmpty(Col(ib).Replace(" ", "")) : null,
                Col(p.TitleColumn),
                p.ReferenceColumn is { } r ? NullIfEmpty(Col(r)) : null));
        }
        if (list.Count == 0) throw new DomainException("Plik nie zawiera operacji.");
        return new ParsedStatement(null, null, list.Min(t => t.BookingDate), list.Max(t => t.BookingDate), null, null, p.DefaultCurrency, list);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// Parser wyciągu w formacie SWIFT MT940 (pola :20:, :25:, :28C:, :60F:, :61:, :86:, :62F:). Struktura pola :86:
/// różni się między bankami — tytuł i dane kontrahenta wyodrębniane są heurystycznie (podpola ~20–~27, ~32–~33, ~38).
/// </summary>
public static class Mt940Parser
{
    public static ParsedStatement Parse(byte[] content)
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = DetectText(content);
        var fields = SplitFields(text);
        string? number = null, iban = null, currency = null;
        decimal? opening = null, closing = null;
        DateOnly? openingDate = null, closingDate = null;
        var tx = new List<ParsedTransaction>();
        ParsedTransaction? pending = null;
        foreach (var (tag, value) in fields)
        {
            switch (tag)
            {
                case "20": break;
                case "25": iban = value.Trim().Replace("/", "").Replace(" ", ""); break;
                case "28C": number = value.Trim(); break;
                case "60F":
                case "60M":
                    (opening, openingDate, currency) = ParseBalance(value); break;
                case "62F":
                case "62M":
                    (closing, closingDate, currency) = ParseBalance(value); break;
                case "61":
                    if (pending is not null) tx.Add(pending);
                    pending = ParseLine61(value, currency ?? "PLN");
                    break;
                case "86":
                    if (pending is not null)
                    {
                        var (title, name, cpIban) = Parse86(value);
                        pending = pending with { Title = title, CounterpartyName = name ?? pending.CounterpartyName, CounterpartyIban = cpIban ?? pending.CounterpartyIban };
                        tx.Add(pending);
                        pending = null;
                    }
                    break;
            }
        }
        if (pending is not null) tx.Add(pending);
        if (tx.Count == 0 && opening is null) throw new DomainException("Plik nie jest prawidłowym wyciągiem MT940.");
        return new ParsedStatement(number, iban, tx.Count > 0 ? tx.Min(t => t.BookingDate) : openingDate, closingDate ?? (tx.Count > 0 ? tx.Max(t => t.BookingDate) : null),
            opening, closing, currency ?? "PLN", tx);
    }

    private static string DetectText(byte[] content)
    {
        try { return new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { return System.Text.Encoding.GetEncoding(852).GetString(content); }
    }

    private static List<(string tag, string value)> SplitFields(string text)
    {
        var result = new List<(string, string)>();
        string? tag = null;
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith(':') && line.IndexOf(':', 1) is var end and > 1 && end <= 5)
            {
                if (tag is not null) result.Add((tag, sb.ToString()));
                tag = line[1..end];
                sb.Clear().Append(line[(end + 1)..]);
            }
            else if (line == "-" || line.StartsWith("{") || line.StartsWith("-}")) { if (tag is not null) { result.Add((tag, sb.ToString())); tag = null; sb.Clear(); } }
            else if (tag is not null) sb.Append('\n').Append(line);
        }
        if (tag is not null) result.Add((tag, sb.ToString()));
        return result;
    }

    private static (decimal, DateOnly, string) ParseBalance(string v)
    {
        // C260131PLN12345,67
        var sign = v[0] == 'D' ? -1 : 1;
        var date = ParseYyMmDd(v.Substring(1, 6));
        var cur = v.Substring(7, 3);
        var amount = decimal.Parse(v[10..].Trim().Replace(',', '.'), CultureInfo.InvariantCulture);
        return (sign * amount, date, cur);
    }

    private static ParsedTransaction ParseLine61(string v, string currency)
    {
        // 2601150115C1230,00NTRFREF123//BANKREF
        var first = v.Split('\n')[0];
        var date = ParseYyMmDd(first[..6]);
        var pos = 6;
        DateOnly? entryDate = null;
        if (first.Length > 10 && char.IsDigit(first[6]) && char.IsDigit(first[9]))
        {
            entryDate = new DateOnly(date.Year, int.Parse(first.Substring(6, 2)), int.Parse(first.Substring(8, 2)));
            pos = 10;
        }
        var reversal = first[pos] == 'R';
        if (reversal) pos++;
        var dc = first[pos];
        pos++;
        if (char.IsLetter(first[pos])) pos++; // trzecia litera kodu waluty (opcjonalna)
        var amountStart = pos;
        while (pos < first.Length && (char.IsDigit(first[pos]) || first[pos] == ',')) pos++;
        var amount = decimal.Parse(first[amountStart..pos].Replace(',', '.'), CultureInfo.InvariantCulture);
        var rest = first[pos..];
        var reference = rest.Length > 4 ? rest[4..] : null;
        var sign = (dc == 'D') ^ reversal ? -1 : 1;
        return new ParsedTransaction(entryDate ?? date, date, sign * amount, currency, null, null, "", reference);
    }

    private static (string title, string? name, string? iban) Parse86(string v)
    {
        var flat = v.Replace("\n", "");
        if (!flat.Contains('~') && !flat.Contains('<')) return (flat.Trim(), null, null);
        var sep = flat.Contains('~') ? '~' : '<';
        var parts = flat.Split(sep).Skip(1).Where(p => p.Length >= 2).Select(p => (code: p[..2], text: p[2..])).ToList();
        string Join(int from, int to) => string.Concat(parts.Where(p => int.TryParse(p.code, out var c) && c >= from && c <= to).Select(p => p.text)).Trim();
        var title = Join(20, 27);
        var name = Join(32, 33);
        var iban = parts.FirstOrDefault(p => p.code == "38").text?.Trim();
        return (string.IsNullOrWhiteSpace(title) ? flat : title, string.IsNullOrWhiteSpace(name) ? null : name, string.IsNullOrWhiteSpace(iban) ? null : iban);
    }

    private static DateOnly ParseYyMmDd(string s) => new(2000 + int.Parse(s[..2]), int.Parse(s.Substring(2, 2)), int.Parse(s.Substring(4, 2)));
}
