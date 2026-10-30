using System.Net;
using System.Text;
using PelnaKsiegowosc.Infrastructure.Counterparties;
using PelnaKsiegowosc.Infrastructure.WhiteList;

namespace PelnaKsiegowosc.Tests.Integrations;

public class CounterpartyVerifierTests
{
    private sealed class Fake(Func<string, (HttpStatusCode, string)> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var (c, b) = f(r.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(c) { Content = new StringContent(b, Encoding.UTF8, "application/json") });
        }
    }

    private static CounterpartyVerifier Make(string krsBody, HttpStatusCode krsCode = HttpStatusCode.OK, string? viesBody = null)
    {
        var wl = new HttpClient(new Fake(_ => (HttpStatusCode.OK, """
            {"result":{"subject":{"name":"ALFA SP. Z O.O.","nip":"7740001454","statusVat":"Czynny","krs":"0000123456","workingAddress":"UL. A 1, 00-001 WARSZAWA","accountNumbers":["12345678901234567890123456"]},"requestId":"R1"}}
            """))) { BaseAddress = WhiteListClient.BaseUrl };
        var krs = new HttpClient(new Fake(p => p.Contains("rejestr=P") ? (krsCode, krsBody) : (HttpStatusCode.NotFound, ""))) { BaseAddress = CounterpartyVerifier.KrsBaseUrl };
        var vies = new HttpClient(new Fake(_ => (HttpStatusCode.OK, viesBody ?? """{"isValid":true,"name":"Beta GmbH","userError":"VALID"}"""))) { BaseAddress = CounterpartyVerifier.ViesBaseUrl };
        return new CounterpartyVerifier(wl, krs, vies);
    }

    [Fact]
    public async Task Company_in_liquidation_is_flagged_as_risk()
    {
        var v = Make("""{"odpis":{"naglowekA":{"numerKRS":"0000123456"},"dane":{"dzial1":{"danePodmiotu":{"nazwa":"ALFA SPÓŁKA Z O.O. W LIKWIDACJI"}},"dzial6":{"likwidacja":{"otwarcieLikwidacji":[{"wpis":"1"}]}}}}}""");
        var r = await v.CheckAsync("7740001454", null, "PL", null, new DateOnly(2026, 10, 30));
        Assert.Equal(CheckLevel.Ryzyko, r.Worst);
        Assert.Contains(r.Lines, l => l.Source == "KRS" && l.Text.Contains("likwidacja"));
        Assert.Contains(r.Lines, l => l.Source == "Biała lista VAT" && l.Level == CheckLevel.Ok);
    }

    [Fact]
    public async Task Clean_company_and_foreign_vies_check()
    {
        var v = Make("""{"odpis":{"dane":{"dzial1":{"danePodmiotu":{"nazwa":"ALFA"}},"dzial6":{}}}}""");
        var r = await v.CheckAsync("7740001454", null, "PL", null, new DateOnly(2026, 10, 30));
        Assert.Equal(CheckLevel.Ok, r.Worst);
        var foreign = await Make("{}", viesBody: """{"isValid":false,"userError":"INVALID"}""").CheckAsync(null, null, "DE", "DE123456789", new DateOnly(2026, 10, 30));
        Assert.Contains(foreign.Lines, l => l.Source == "VIES" && l.Level == CheckLevel.Ryzyko);
    }

    [Fact]
    public async Task Missing_krs_entry_is_risk_and_network_errors_are_informational()
    {
        var r = await Make("", HttpStatusCode.NotFound).CheckAsync("7740001454", null, "PL", null, new DateOnly(2026, 10, 30));
        Assert.Contains(r.Lines, l => l.Source == "KRS" && l.Level == CheckLevel.Ryzyko);
        var err = await Make("", HttpStatusCode.InternalServerError).CheckAsync("7740001454", null, "PL", null, new DateOnly(2026, 10, 30));
        Assert.Contains(err.Lines, l => l.Source == "KRS" && l.Level == CheckLevel.Informacja);
    }
}
