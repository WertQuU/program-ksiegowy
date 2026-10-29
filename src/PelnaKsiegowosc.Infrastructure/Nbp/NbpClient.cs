using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Infrastructure.Nbp;

public sealed record NbpRate(string Currency, decimal Mid, string TableNumber, DateOnly EffectiveDate);

/// <summary>
/// Średnie kursy NBP (tabela A, https://api.nbp.pl). Do przeliczeń stosuje się kurs z ostatniego dnia roboczego
/// poprzedzającego dzień operacji / powstania obowiązku podatkowego (art. 31a ust. 1 ustawy o VAT, art. 30 ust. 2 UoR,
/// art. 15a ustawy o CIT). Gdy w danym dniu nie opublikowano tabeli, sprawdzane są kolejne dni wstecz.
/// </summary>
public sealed class NbpClient
{
    public static readonly Uri BaseUrl = new("https://api.nbp.pl/");
    private readonly HttpClient _http;

    public NbpClient(HttpClient http) => _http = http;

    private sealed record RateDto(string No, string EffectiveDate, decimal Mid);
    private sealed record TableDto(string Code, List<RateDto> Rates);

    /// <summary>Kurs z ostatniej tabeli opublikowanej przed dniem <paramref name="operationDate"/>.</summary>
    public async Task<NbpRate> RateBeforeAsync(string currency, DateOnly operationDate, CancellationToken ct = default)
    {
        var code = currency.Trim().ToUpperInvariant();
        if (code.Length != 3 || code == "PLN") throw new DomainException("Podaj kod waluty obcej (np. EUR, USD).");
        for (var d = operationDate.AddDays(-1); d >= operationDate.AddDays(-10); d = d.AddDays(-1))
        {
            using var resp = await _http.GetAsync($"api/exchangerates/rates/a/{code.ToLowerInvariant()}/{d:yyyy-MM-dd}/?format=json", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) continue;
            if (!resp.IsSuccessStatusCode) throw new DomainException($"NBP: błąd {(int)resp.StatusCode} przy pobieraniu kursu {code}.");
            var t = await resp.Content.ReadFromJsonAsync<TableDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
                    ?? throw new DomainException("NBP: pusta odpowiedź.");
            var r = t.Rates.First();
            return new NbpRate(code, r.Mid, r.No, DateOnly.ParseExact(r.EffectiveDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }
        throw new DomainException($"NBP nie publikuje kursu {code} w tabeli A albo brak tabel z ostatnich 10 dni.");
    }
}
