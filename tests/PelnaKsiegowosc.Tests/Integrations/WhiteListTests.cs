using System.Net;
using System.Text;
using PelnaKsiegowosc.Infrastructure.WhiteList;

namespace PelnaKsiegowosc.Tests.Integrations;

public class WhiteListTests
{
    private sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> f) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Paths.Add(r.RequestUri!.PathAndQuery);
            var (code, body) = f(r);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Search_by_nip_maps_subject_and_parses_address()
    {
        var fake = new Fake(_ => (HttpStatusCode.OK, """
            {"result":{"subject":{"name":"PRZYKŁAD SPÓŁKA Z OGRANICZONĄ ODPOWIEDZIALNOŚCIĄ","nip":"7740001454","statusVat":"Czynny","regon":"610188201",
             "krs":"0000028860","residenceAddress":null,"workingAddress":"UL. DŁUGA 5/3, 00-950 WARSZAWA",
             "accountNumbers":["12345678901234567890123456"],"hasVirtualAccounts":false},"requestId":"Ab1-2","requestDateTime":"29-10-2026 10:00:00"}}
            """));
        using var http = new HttpClient(fake) { BaseAddress = WhiteListClient.BaseUrl };
        var s = await new WhiteListClient(http).FindByNipAsync("774-000-14-54", new DateOnly(2026, 10, 29));
        Assert.NotNull(s);
        Assert.True(s!.IsActiveVatPayer);
        Assert.Equal("Ab1-2", s.RequestId);
        Assert.Single(s.AccountNumbers);
        Assert.Equal("/api/search/nip/7740001454?date=2026-10-29", fake.Paths.Single());
        var a = WhiteListClient.ParseAddress(s.Address)!;
        Assert.Equal(("ul. Długa", "5", "3", "00-950", "Warszawa"), (a.Street, a.Building, a.Apartment, a.PostalCode, a.City));
    }

    [Fact]
    public async Task Account_check_and_errors()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath.Contains("bank-account")
            ? (HttpStatusCode.OK, """{"result":{"accountAssigned":"NIE","requestId":"X9"}}""")
            : (HttpStatusCode.BadRequest, """{"code":"WL-113","message":"Nieprawidłowy NIP"}"""));
        using var http = new HttpClient(fake) { BaseAddress = WhiteListClient.BaseUrl };
        var c = new WhiteListClient(http);
        var (assigned, id) = await c.CheckAccountAsync("7740001454", "PL12 3456 7890 1234 5678 9012 3456", new DateOnly(2026, 10, 29));
        Assert.False(assigned);
        Assert.Equal("X9", id);
        Assert.Contains("/bank-account/12345678901234567890123456", fake.Paths.Last());
        var ex = await Assert.ThrowsAsync<PelnaKsiegowosc.Domain.Common.DomainException>(() => c.FindByNipAsync("7740001454", new DateOnly(2026, 10, 29)));
        Assert.Contains("WL-113", ex.Message);
        await Assert.ThrowsAsync<PelnaKsiegowosc.Domain.Common.DomainException>(() => c.FindByNipAsync("1234567890", new DateOnly(2026, 10, 29)));
    }

    [Fact]
    public void Bank_operation_types_point_to_template_accounts()
    {
        var all = PelnaKsiegowosc.Application.Templates.ChartTemplate.Build(PelnaKsiegowosc.Domain.Companies.LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia,
            new PelnaKsiegowosc.Application.Templates.ChartOptions(Employees: true, BankLoans: true)).Select(a => a.Number).ToHashSet();
        foreach (var t in PelnaKsiegowosc.Application.Templates.BankOperationTypes.All) Assert.Contains(t.AccountNumber, all);
    }
}
