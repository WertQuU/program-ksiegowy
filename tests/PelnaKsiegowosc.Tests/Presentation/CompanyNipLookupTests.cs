using System.Net;
using System.Text;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.WhiteList;
using PelnaKsiegowosc.Presentation.ViewModels;

namespace PelnaKsiegowosc.Tests.Presentation;

public class CompanyNipLookupTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return respond(request); }
    }
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private const string Found = """
        {"result":{"subject":{"name":"Testowa sp. z o.o.","nip":"7740001454","regon":"123456789","krs":"0000123456",
        "workingAddress":"UL. DŁUGA 5/3, 00-001 WARSZAWA","accountNumbers":["12345678901234567890123456"]},"requestId":"REQ-1"}}
        """;

    [Fact]
    public async Task Lookup_fills_company_identity_and_address_without_changing_legal_or_tax_choices()
    {
        var handler = new Handler(request =>
        {
            Assert.Contains("api/search/nip/7740001454?date=2026-11-01", request.RequestUri!.ToString());
            return Task.FromResult(Reply(Found));
        });
        using var http = new HttpClient(handler) { BaseAddress = WhiteListClient.BaseUrl };
        var form = new CompanyForm { Nip = "774-000-14-54" };
        var originalLegalForm = form.LegalForm;
        var originalVatStatus = form.VatStatus;
        await form.FetchByNipAsync(new WhiteListClient(http), new DateOnly(2026, 11, 1));
        Assert.Equal("7740001454", form.Nip);
        Assert.Equal("Testowa sp. z o.o.", form.FullName);
        Assert.Equal("0000123456", form.Krs);
        Assert.Equal("ul. Długa", form.Street);
        Assert.Equal("5", form.BuildingNumber);
        Assert.Equal("3", form.ApartmentNumber);
        Assert.Equal("00-001", form.PostalCode);
        Assert.Equal("Warszawa", form.City);
        Assert.Contains("REQ-1", form.LookupInfo);
        Assert.Same(originalLegalForm, form.LegalForm);
        Assert.Same(originalVatStatus, form.VatStatus);
        Assert.Null(form.TaxOfficeCode);
    }

    [Fact]
    public async Task Lookup_preserves_typed_fields_and_does_not_choose_between_multiple_bank_accounts()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply(Found.Replace(
            "[\"12345678901234567890123456\"]", "[\"12345678901234567890123456\",\"22345678901234567890123456\"]")))))
            { BaseAddress = WhiteListClient.BaseUrl };
        var form = new CompanyForm { Nip = "7740001454", FullName = "Własna nazwa", City = "Kraków" };
        await form.FetchByNipAsync(new WhiteListClient(http), new DateOnly(2026, 11, 1));
        Assert.Equal("Własna nazwa", form.FullName);
        Assert.Equal("Kraków", form.City);
        Assert.Null(form.BankIban);
        Assert.Contains("kilka rachunków", form.LookupInfo);
    }

    [Fact]
    public async Task Missing_company_leaves_form_editable_and_unchanged()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply("{\"result\":{\"subject\":null}}"))))
            { BaseAddress = WhiteListClient.BaseUrl };
        var form = new CompanyForm { Nip = "7740001454", FullName = "Ręcznie" };
        await form.FetchByNipAsync(new WhiteListClient(http), new DateOnly(2026, 11, 1));
        Assert.Equal("Ręcznie", form.FullName);
        Assert.Contains("ręcznie", form.LookupInfo);
    }

    [Fact]
    public async Task Invalid_nip_never_sends_request_and_wrong_returned_identity_never_fills_fields()
    {
        var handler = new Handler(_ => Task.FromResult(Reply(Found.Replace("7740001454", "5260250274"))));
        using var http = new HttpClient(handler) { BaseAddress = WhiteListClient.BaseUrl };
        var form = new CompanyForm { Nip = "123" };
        await Assert.ThrowsAsync<DomainException>(() => form.FetchByNipAsync(new WhiteListClient(http), new DateOnly(2026, 11, 1)));
        Assert.Equal(0, handler.Calls);
        form.Nip = "7740001454";
        await Assert.ThrowsAsync<DomainException>(() => form.FetchByNipAsync(new WhiteListClient(http), new DateOnly(2026, 11, 1)));
        Assert.Empty(form.FullName);
    }

    [Fact]
    public async Task Changing_nip_during_lookup_discards_stale_response()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>();
        using var http = new HttpClient(new Handler(_ => completion.Task)) { BaseAddress = WhiteListClient.BaseUrl };
        var form = new CompanyForm { Nip = "7740001454" };
        var pending = form.FetchByNipAsync(new WhiteListClient(http), new DateOnly(2026, 11, 1));
        form.Nip = "5260250274";
        completion.SetResult(Reply(Found));
        await Assert.ThrowsAsync<DomainException>(() => pending);
        Assert.Empty(form.FullName);
        Assert.Equal("5260250274", form.Nip);
    }
}
