using System.Globalization;
using System.Text.Json;
using PelnaKsiegowosc.Domain.Legal;

namespace PelnaKsiegowosc.Infrastructure.Legal;

/// <summary>Zmiana w monitorowanym akcie: akt zmieniający, nowy tekst jednolity lub zmiana danych aktu.</summary>
public sealed record LegalChange(string ActEliId, string ActName, string Area, string ChangeEliId, string Kind, string Title,
    DateOnly? Published, DateOnly? InForce, string Url);

/// <summary>
/// Monitor zmian w przepisach na podstawie publicznego ELI API Sejmu (https://api.sejm.gov.pl/eli). Dla każdego
/// monitorowanego aktu odczytuje powiązania („Akty zmieniające”, teksty jednolite) i zwraca te opublikowane po dacie
/// weryfikacji rejestru przepisów programu. Nie interpretuje treści — wskazuje, co należy przejrzeć.
/// </summary>
public sealed class LegalMonitor
{
    public static readonly Uri BaseUrl = new("https://api.sejm.gov.pl/");
    private readonly HttpClient _http;

    public LegalMonitor(HttpClient http) => _http = http;

    public async Task<List<LegalChange>> CheckAsync(DateOnly since, IEnumerable<MonitoredAct> acts, CancellationToken ct = default)
    {
        var result = new List<LegalChange>();
        foreach (var act in acts)
        {
            using var doc = await GetJsonAsync($"eli/acts/{act.EliId}", ct);
            if (doc is null) continue;
            var root = doc.RootElement;
            var found = 0;
            if (root.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Object)
            {
                foreach (var group in refs.EnumerateObject())
                {
                    var name = group.Name.ToLowerInvariant();
                    var kind = name.Contains("zmieni") && !name.Contains("zmieniane") ? "akt zmieniający"
                        : name.Contains("jednolit") ? "tekst jednolity" : null;
                    if (kind is null || group.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var e in group.Value.EnumerateArray())
                    {
                        var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var refDate = Date(e, "date");
                        if (refDate is { } rd && rd <= since) continue;
                        var details = await DetailsAsync(id, ct);
                        var published = details.published ?? refDate;
                        if (published is { } p && p <= since) continue;
                        if (published is null && refDate is null) continue;
                        result.Add(new LegalChange(act.EliId, act.Name, act.Area, id, kind, details.title ?? id, published, details.inForce, IsapUrl(id)));
                        found++;
                    }
                }
            }
            if (found == 0 && DateTimeOffset.TryParse(root.TryGetProperty("changeDate", out var cd) ? cd.GetString() : null, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var changed)
                && DateOnly.FromDateTime(changed.UtcDateTime) > since)
                result.Add(new LegalChange(act.EliId, act.Name, act.Area, act.EliId, "zmiana danych aktu",
                    "Zaktualizowano informacje o akcie w systemie ISAP — sprawdź, czy opublikowano zmiany lub tekst jednolity.",
                    DateOnly.FromDateTime(changed.UtcDateTime), null, IsapUrl(act.EliId)));
        }
        return result.OrderByDescending(c => c.Published).ToList();
    }

    private async Task<(string? title, DateOnly? published, DateOnly? inForce)> DetailsAsync(string id, CancellationToken ct)
    {
        using var d = await GetJsonAsync($"eli/acts/{id}", ct);
        if (d is null) return (null, null, null);
        var r = d.RootElement;
        return (r.TryGetProperty("title", out var t) ? t.GetString() : null,
            Date(r, "announcementDate") ?? Date(r, "promulgation"),
            Date(r, "entryIntoForce") ?? Date(r, "validFrom"));
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static DateOnly? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateOnly.TryParse(v.GetString()?[..Math.Min(10, v.GetString()!.Length)], CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Adres strony aktu w ISAP (identyfikator WDU + rok + pozycja dla aktów od 2012 r.).</summary>
    public static string IsapUrl(string eliId)
    {
        var p = eliId.Split('/');
        if (p.Length == 3 && int.TryParse(p[1], out var y) && int.TryParse(p[2], out var pos) && y >= 2012)
            return $"https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=W{p[0]}{y}{pos:0000000}";
        return $"https://api.sejm.gov.pl/eli/acts/{eliId}/text.pdf";
    }
}
