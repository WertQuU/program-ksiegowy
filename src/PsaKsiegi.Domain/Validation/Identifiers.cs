using System.Numerics;

namespace PsaKsiegi.Domain.Validation;

/// <summary>
/// Walidacja formalna identyfikatorów (sumy kontrolne). Aplikacja nigdy nie generuje numerów
/// rejestrowych — sprawdza jedynie poprawność formatu wpisanych przez użytkownika danych.
/// </summary>
public static class Identifiers
{
    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>NIP: 10 cyfr, wagi 6,5,7,2,3,4,5,6,7, suma mod 11 = cyfra kontrolna (10 niedopuszczalne).</summary>
    public static bool IsValidNip(string? nip)
    {
        var d = Digits(nip);
        if (d.Length != 10) return false;
        var raw = nip!.Trim();
        if (raw.StartsWith("PL", StringComparison.OrdinalIgnoreCase)) raw = raw[2..];
        if (raw.Any(char.IsLetter)) return false;
        int[] w = { 6, 5, 7, 2, 3, 4, 5, 6, 7 };
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (d[i] - '0') * w[i];
        var c = sum % 11;
        return c != 10 && c == d[9] - '0';
    }

    public static string NormalizeNip(string? nip) => Digits(nip);

    /// <summary>REGON 9- lub 14-cyfrowy.</summary>
    public static bool IsValidRegon(string? regon)
    {
        var d = Digits(regon);
        if (d.Length == 9)
            return Check(d, new[] { 8, 9, 2, 3, 4, 5, 6, 7 });
        if (d.Length == 14)
            return Check(d[..9], new[] { 8, 9, 2, 3, 4, 5, 6, 7 }) &&
                   Check(d, new[] { 2, 4, 8, 5, 0, 9, 7, 3, 6, 1, 2, 4, 8 });
        return false;

        static bool Check(string digits, int[] weights)
        {
            var sum = 0;
            for (var i = 0; i < weights.Length; i++) sum += (digits[i] - '0') * weights[i];
            var c = sum % 11;
            if (c == 10) c = 0;
            return c == digits[weights.Length] - '0';
        }
    }

    /// <summary>KRS: 10 cyfr (brak sumy kontrolnej — sprawdzany tylko format).</summary>
    public static bool IsValidKrs(string? krs)
    {
        var t = (krs ?? "").Trim();
        return t.Length == 10 && t.All(char.IsDigit);
    }

    /// <summary>PESEL — suma kontrolna (używane np. dla akcjonariuszy będących osobami fizycznymi).</summary>
    public static bool IsValidPesel(string? pesel)
    {
        var d = Digits(pesel);
        if (d.Length != 11) return false;
        int[] w = { 1, 3, 7, 9, 1, 3, 7, 9, 1, 3 };
        var sum = 0;
        for (var i = 0; i < 10; i++) sum += (d[i] - '0') * w[i];
        return (10 - sum % 10) % 10 == d[10] - '0';
    }

    /// <summary>IBAN (ISO 13616, mod 97). Polski numer NRB (26 cyfr) jest akceptowany i traktowany jako PL.</summary>
    public static bool IsValidIban(string? iban)
    {
        var t = NormalizeIban(iban);
        if (t.Length < 15 || t.Length > 34) return false;
        if (t.StartsWith("PL") && t.Length != 28) return false;
        var rearranged = t[4..] + t[..4];
        var numeric = string.Concat(rearranged.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        if (!numeric.All(char.IsDigit)) return false;
        return BigInteger.Parse(numeric) % 97 == 1;
    }

    public static string NormalizeIban(string? iban)
    {
        var t = new string((iban ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (t.Length == 26 && t.All(char.IsDigit)) t = "PL" + t;
        return t;
    }

    /// <summary>Kod pocztowy PL: 00-000.</summary>
    public static bool IsValidPostalCode(string? code) =>
        code is { Length: 6 } && char.IsDigit(code[0]) && char.IsDigit(code[1]) && code[2] == '-' && code[3..].All(char.IsDigit);
}
