using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Infrastructure.WhiteList;

/// <summary>Dane podmiotu z wykazu podatników VAT („biała lista”, art. 96b ustawy o VAT).</summary>
public sealed record WhiteListSubject(string Name, string Nip, string? StatusVat, string? Regon, string? Krs, string? Address,
    IReadOnlyList<string> AccountNumbers, DateOnly CheckDate, string? RequestId)
{
    public bool IsActiveVatPayer => string.Equals(StatusVat, "Czynny", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Adres z białej listy rozdzielony na pola formularza („UL. DŁUGA 5/3, 00-001 WARSZAWA”).</summary>
public sealed record ParsedAddress(string Street, string Building, string? Apartment, string PostalCode, string City);

/// <summary>
/// Klient publicznego API wykazu podatników VAT (https://wl-api.mf.gov.pl). Wyszukanie po NIP i sprawdzenie rachunku
/// bankowego na dzień — zapłata ponad 15 000 zł na rachunek spoza wykazu może wyłączyć wydatek z kosztów (art. 15d ust. 1 pkt 3
/// ustawy o CIT) i powodować solidarną odpowiedzialność za VAT (art. 117ba Ordynacji podatkowej). Identyfikator zapytania
/// (requestId) warto zachować jako dowód sprawdzenia.
/// </summary>
public sealed class WhiteListClient
{
    public static readonly Uri BaseUrl = new("https://wl-api.mf.gov.pl/");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _http;

    public WhiteListClient(HttpClient http) => _http = http;

    private sealed record SubjectDto(string? Name, string? Nip, string? StatusVat, string? Regon, string? Krs, string? WorkingAddress, string? ResidenceAddress, List<string>? AccountNumbers);
    private sealed record SearchResult(SubjectDto? Subject, string? RequestId);
    private sealed record SearchResponse(SearchResult? Result);
    private sealed record CheckResult(string? AccountAssigned, string? RequestId);
    private sealed record CheckResponse(CheckResult? Result);
    private sealed record ErrorResponse(string? Code, string? Message);

    public async Task<WhiteListSubject?> FindByNipAsync(string nip, DateOnly date, CancellationToken ct = default)
    {
        var n = Identifiers.NormalizeNip(nip);
        if (!Identifiers.IsValidNip(n)) throw new DomainException("Nieprawidłowy NIP.");
        using var resp = await _http.GetAsync($"api/search/nip/{n}?date={date:yyyy-MM-dd}", ct);
        if (!resp.IsSuccessStatusCode) throw new DomainException("Biała lista: " + await ErrorAsync(resp, ct));
        var r = await resp.Content.ReadFromJsonAsync<SearchResponse>(Json, ct);
        var s = r?.Result?.Subject;
        if (s is null) return null;
        if (!string.IsNullOrWhiteSpace(s.Nip) && Identifiers.NormalizeNip(s.Nip) != n)
            throw new DomainException("Wykaz MF zwrócił dane dla innego NIP. Dane nie zostały pobrane do formularza.");
        return new WhiteListSubject(s.Name ?? "", s.Nip ?? n, s.StatusVat, s.Regon, s.Krs, s.WorkingAddress ?? s.ResidenceAddress,
            (s.AccountNumbers ?? new()).ToList(), date, r!.Result!.RequestId);
    }

    /// <summary>Czy rachunek jest przypisany do podatnika w wykazie (TAK/NIE) wraz z identyfikatorem zapytania.</summary>
    public async Task<(bool assigned, string? requestId)> CheckAccountAsync(string nip, string iban, DateOnly date, CancellationToken ct = default)
    {
        var n = Identifiers.NormalizeNip(nip);
        var account = new string(iban.Where(char.IsDigit).ToArray());
        if (account.Length != 26) throw new DomainException("Biała lista obejmuje polskie rachunki (26 cyfr NRB).");
        using var resp = await _http.GetAsync($"api/check/nip/{n}/bank-account/{account}?date={date:yyyy-MM-dd}", ct);
        if (!resp.IsSuccessStatusCode) throw new DomainException("Biała lista: " + await ErrorAsync(resp, ct));
        var r = await resp.Content.ReadFromJsonAsync<CheckResponse>(Json, ct);
        return (string.Equals(r?.Result?.AccountAssigned, "TAK", StringComparison.OrdinalIgnoreCase), r?.Result?.RequestId);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var e = await resp.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct);
            if (!string.IsNullOrWhiteSpace(e?.Message)) return $"{e.Message} ({e.Code})";
        }
        catch (JsonException) { }
        return $"HTTP {(int)resp.StatusCode}";
    }

    public static ParsedAddress? ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var comma = address.LastIndexOf(',');
        if (comma < 0) return null;
        var left = address[..comma].Trim();
        var right = address[(comma + 1)..].Trim();
        var m = System.Text.RegularExpressions.Regex.Match(right, @"^(\d{2}-\d{3})\s+(.+)$");
        if (!m.Success) return null;
        string Title(string s) => System.Globalization.CultureInfo.GetCultureInfo("pl-PL").TextInfo.ToTitleCase(s.ToLower(System.Globalization.CultureInfo.GetCultureInfo("pl-PL")));
        var parts = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var number = parts.Length > 1 && char.IsDigit(parts[^1][0]) ? parts[^1] : "";
        var street = number.Length > 0 ? string.Join(' ', parts[..^1]) : left;
        string? apt = null;
        if (number.Contains('/')) { apt = number[(number.IndexOf('/') + 1)..]; number = number[..number.IndexOf('/')]; }
        street = Title(street).Replace("Ul. ", "ul. ").Replace("Al. ", "al. ").Replace("Pl. ", "pl. ").Replace("Os. ", "os. ");
        return new ParsedAddress(street, number, apt, m.Groups[1].Value, Title(m.Groups[2].Value));
    }
}
