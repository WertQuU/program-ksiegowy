using System.Globalization;

namespace PsaKsiegi.Domain.Common;

/// <summary>
/// Operacje pieniężne. Wszystkie kwoty są typu <see cref="decimal"/> — nigdy zmiennoprzecinkowe.
/// Zaokrąglanie: „do pełnych groszy”, połówki w górę (od zera) — zgodnie z praktyką
/// art. 106e ust. 11 ustawy o VAT (kwoty na fakturze) oraz art. 63 Ordynacji podatkowej
/// (zaokrąglanie podstaw i kwot podatków do pełnych złotych).
/// </summary>
public static class Money
{
    public const int Scale = 2;

    public static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>Zaokrąglenie do groszy (połówki od zera).</summary>
    public static decimal Round(decimal value) => Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    /// <summary>Zaokrąglenie do pełnych złotych (art. 63 § 1 Ordynacji podatkowej: końcówki &lt; 50 gr pomija się, ≥ 50 gr podwyższa do pełnych złotych).</summary>
    public static decimal RoundToFullZloty(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);

    public static bool HasMoreThanTwoDecimals(decimal value) => Round(value) != value;

    /// <summary>Przeliczenie kwoty walutowej na PLN po kursie (np. średnim NBP), wynik zaokrąglony do groszy.</summary>
    public static decimal ToPln(decimal amountInCurrency, decimal exchangeRate)
    {
        if (exchangeRate <= 0) throw new DomainException("Kurs waluty musi być dodatni.");
        return Round(amountInCurrency * exchangeRate);
    }

    /// <summary>Format polski: 1 234 567,89</summary>
    public static string Format(decimal value) => value.ToString("#,##0.00", Pl);

    public static string FormatWithCurrency(decimal value, string currency = "PLN") => $"{Format(value)} {currency}";

    /// <summary>Format do plików XML (kropka dziesiętna, 2 miejsca).</summary>
    public static string Xml(decimal value) => Round(value).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parsuje kwotę zapisaną po polsku („1 234,56”, „1234,56”, „-12,5”) lub z kropką dziesiętną.
    /// </summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "");
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        var lastComma = t.LastIndexOf(',');
        var lastDot = t.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            // separator dziesiętny to ten, który występuje jako ostatni
            if (lastComma > lastDot) t = t.Replace(".", "").Replace(',', '.');
            else t = t.Replace(",", "");
        }
        else if (lastComma >= 0)
        {
            t = t.Replace(',', '.');
        }
        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        if (negative) value = -value;
        return true;
    }

    public static decimal Parse(string text) =>
        TryParse(text, out var v) ? v : throw new DomainException($"Nieprawidłowa kwota: „{text}”.");
}
