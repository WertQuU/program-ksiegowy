using System.Net;
using System.Text.Json;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Infrastructure.WhiteList;

namespace PelnaKsiegowosc.Infrastructure.Counterparties;

public enum CheckLevel { Ok, Informacja, Uwaga, Ryzyko }

public sealed record CheckLine(CheckLevel Level, string Source, string Text);

/// <summary>Wynik weryfikacji kontrahenta z bezpłatnych rejestrów publicznych.</summary>
public sealed class CounterpartyCheckReport
{
    public List<CheckLine> Lines { get; } = new();
    public WhiteListSubject? WhiteList { get; set; }
    public string? KrsName { get; set; }
    public CheckLevel Worst => Lines.Count == 0 ? CheckLevel.Informacja : Lines.Max(l => l.Level);

    public string Summary => Worst switch
    {
        CheckLevel.Ryzyko => "RYZYKO — przed współpracą sprawdź szczegóły",
        CheckLevel.Uwaga => "Uwaga — są informacje wymagające sprawdzenia",
        CheckLevel.Ok => "Nie znaleziono negatywnych informacji w sprawdzonych rejestrach",
        _ => "Sprawdzenie niepełne"
    };

    public override string ToString() =>
        Summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, Lines.Select(l => $"[{l.Level.ToString().ToUpperInvariant()}] {l.Source}: {l.Text}")) +
        Environment.NewLine + Environment.NewLine +
        "Sprawdzono bezpłatne rejestry publiczne. Zadłużenie wobec innych firm (BIG, KRD) jest dostępne tylko odpłatnie i nie jest sprawdzane. " +
        "Postępowania restrukturyzacyjne i upadłościowe osób i firm sprawdzisz w Krajowym Rejestrze Zadłużonych (przycisk „Otwórz KRZ”).";
}

/// <summary>
/// Weryfikacja kontrahenta w bezpłatnych rejestrach: wykaz podatników VAT (biała lista), KRS (odpis aktualny — wykreślenie,
/// likwidacja, upadłość, restrukturyzacja w dziale 6), VIES (numer VAT UE). Niedostępność rejestru nie przerywa sprawdzenia.
/// </summary>
public sealed class CounterpartyVerifier
{
    public static readonly Uri KrsBaseUrl = new("https://api-krs.ms.gov.pl/");
    public static readonly Uri ViesBaseUrl = new("https://ec.europa.eu/");
    public const string KrzUrl = "https://krz.ms.gov.pl/";

    private readonly HttpClient _whiteList;
    private readonly HttpClient _krs;
    private readonly HttpClient _vies;

    public CounterpartyVerifier(HttpClient whiteList, HttpClient krs, HttpClient vies)
    {
        _whiteList = whiteList;
        _krs = krs;
        _vies = vies;
    }

    public async Task<CounterpartyCheckReport> CheckAsync(string? nip, string? krs, string? countryCode, string? vatEuNumber, DateOnly today, CancellationToken ct = default)
    {
        var r = new CounterpartyCheckReport();
        var country = string.IsNullOrWhiteSpace(countryCode) ? "PL" : countryCode.Trim().ToUpperInvariant();
        if (country == "PL" && !string.IsNullOrWhiteSpace(nip))
        {
            try
            {
                var s = await new WhiteListClient(_whiteList).FindByNipAsync(nip, today, ct);
                r.WhiteList = s;
                if (s is null)
                    r.Lines.Add(new(CheckLevel.Uwaga, "Biała lista VAT", "Brak podmiotu w wykazie podatników VAT — firma nie jest czynnym podatnikiem VAT (VAT z jej faktur zwykle nie podlega odliczeniu) albo NIP jest błędny."));
                else
                {
                    r.Lines.Add(s.IsActiveVatPayer
                        ? new(CheckLevel.Ok, "Biała lista VAT", $"{s.Name}: czynny podatnik VAT, rachunków w wykazie: {s.AccountNumbers.Count} (zapytanie {s.RequestId}).")
                        : new(CheckLevel.Ryzyko, "Biała lista VAT", $"{s.Name}: status VAT — {s.StatusVat ?? "brak"}. Faktury VAT od podmiotu niebędącego czynnym podatnikiem nie dają prawa do odliczenia."));
                    if (s.AccountNumbers.Count == 0 && s.IsActiveVatPayer)
                        r.Lines.Add(new(CheckLevel.Uwaga, "Biała lista VAT", "Podmiot nie ma rachunków w wykazie — płatność ponad 15 000 zł wymaga zgłoszenia ZAW-NR."));
                    krs ??= s.Krs;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or Domain.Common.DomainException)
            {
                r.Lines.Add(new(CheckLevel.Informacja, "Biała lista VAT", "Nie udało się sprawdzić: " + ex.Message));
            }
        }

        if (!string.IsNullOrWhiteSpace(krs) && Identifiers.IsValidKrs(krs.Trim()))
            await CheckKrsAsync(krs.Trim(), r, ct);
        else if (country == "PL")
            r.Lines.Add(new(CheckLevel.Informacja, "KRS", "Brak numeru KRS — podmiot nie jest spółką/fundacją w KRS (np. działalność jednoosobowa — sprawdź CEIDG) albo numer nie jest znany."));

        if (country != "PL" || !string.IsNullOrWhiteSpace(vatEuNumber))
            await CheckViesAsync(country, vatEuNumber ?? nip, r, ct);
        return r;
    }

    private async Task CheckKrsAsync(string krs, CounterpartyCheckReport r, CancellationToken ct)
    {
        try
        {
            JsonDocument? doc = null;
            foreach (var reg in new[] { "P", "S" })
            {
                using var resp = await _krs.GetAsync($"api/krs/OdpisAktualny/{krs}?rejestr={reg}&format=json", ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                break;
            }
            if (doc is null)
            {
                r.Lines.Add(new(CheckLevel.Ryzyko, "KRS", $"Nie znaleziono aktualnego wpisu KRS {krs} — podmiot mógł zostać wykreślony albo numer jest błędny."));
                return;
            }
            using (doc)
            {
                var odpis = doc.RootElement.TryGetProperty("odpis", out var o) ? o : doc.RootElement;
                var dane = odpis.TryGetProperty("dane", out var d) ? d : default;
                if (dane.ValueKind == JsonValueKind.Object && dane.TryGetProperty("dzial1", out var d1) && d1.TryGetProperty("danePodmiotu", out var dp) && dp.TryGetProperty("nazwa", out var nazwa))
                    r.KrsName = nazwa.GetString();
                if (odpis.TryGetProperty("naglowekA", out var nag) && nag.TryGetProperty("dataWykreslenia", out var wykr) && wykr.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(wykr.GetString()))
                {
                    r.Lines.Add(new(CheckLevel.Ryzyko, "KRS", $"Podmiot wykreślony z KRS ({wykr.GetString()})."));
                    return;
                }
                var flags = new List<string>();
                if (dane.ValueKind == JsonValueKind.Object && dane.TryGetProperty("dzial6", out var d6) && d6.ValueKind == JsonValueKind.Object)
                    foreach (var p in d6.EnumerateObject())
                    {
                        var n = p.Name.ToLowerInvariant();
                        var label = n.Contains("likwidac") ? "likwidacja" : n.Contains("upadl") ? "upadłość" : n.Contains("restruktur") || n.Contains("uklad") ? "restrukturyzacja / układ"
                            : n.Contains("rozwiaz") ? "rozwiązanie lub unieważnienie" : n.Contains("zawiesz") ? "zawieszenie działalności" : n.Contains("zarzad") && n.Contains("przymus") ? "zarząd przymusowy" : null;
                        if (label is not null && p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && p.Value.ToString().Length > 2) flags.Add(label);
                    }
                if (flags.Count > 0)
                    r.Lines.Add(new(CheckLevel.Ryzyko, "KRS", $"{r.KrsName ?? krs}: wpis w dziale 6 KRS — {string.Join(", ", flags.Distinct())}."));
                else
                    r.Lines.Add(new(CheckLevel.Ok, "KRS", $"{r.KrsName ?? krs}: aktywny wpis, bez informacji o likwidacji, upadłości ani restrukturyzacji w dziale 6."));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            r.Lines.Add(new(CheckLevel.Informacja, "KRS", "Nie udało się sprawdzić: " + ex.Message));
        }
    }

    private async Task CheckViesAsync(string country, string? number, CounterpartyCheckReport r, CancellationToken ct)
    {
        var digits = new string((number ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (digits.StartsWith(country)) digits = digits[country.Length..];
        if (digits.Length == 0) { r.Lines.Add(new(CheckLevel.Informacja, "VIES", "Brak numeru VAT UE do sprawdzenia.")); return; }
        var cc = country == "GR" ? "EL" : country;
        try
        {
            using var resp = await _vies.GetAsync($"taxation_customs/vies/rest-api/ms/{cc}/vat/{digits}", ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            var valid = (root.TryGetProperty("isValid", out var v) || root.TryGetProperty("valid", out v)) && v.ValueKind == JsonValueKind.True;
            var name = root.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            var error = root.TryGetProperty("userError", out var ue) ? ue.GetString() : null;
            if (valid)
                r.Lines.Add(new(CheckLevel.Ok, "VIES", $"Numer {cc}{digits} jest aktywny w VIES{(string.IsNullOrWhiteSpace(name) || name == "---" ? "" : $" ({name})")} — można stosować stawki dla transakcji wewnątrzwspólnotowych."));
            else if (error is not null && error != "INVALID" && error != "VALID")
                r.Lines.Add(new(CheckLevel.Informacja, "VIES", $"Usługa państwa członkowskiego niedostępna ({error}) — spróbuj później."));
            else
                r.Lines.Add(new(CheckLevel.Ryzyko, "VIES", $"Numer {cc}{digits} NIE jest aktywny w VIES — stawka 0% WDT i odwrotne obciążenie usług mogą być zakwestionowane."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            r.Lines.Add(new(CheckLevel.Informacja, "VIES", "Nie udało się sprawdzić: " + ex.Message));
        }
    }
}
