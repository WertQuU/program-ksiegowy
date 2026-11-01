using System.Net;
using System.Text;
using System.Text.Json;
using PelnaKsiegowosc.Infrastructure.Ksef;

namespace PelnaKsiegowosc.Tests.Integrations;

/// <summary>
/// KSeF 2.0: ograniczenie 10 000 rekordów (isTruncated) i limit stron nie mogą
/// powodować pozornie udanej, ale niekompletnej synchronizacji faktur zakupowych.
/// </summary>
public class KsefMetadataPaginationTests
{
    private sealed class MetadataHandler : HttpMessageHandler
    {
        private readonly Func<int, KsefDto.QueryResponse> _response;

        public MetadataHandler(Func<int, KsefDto.QueryResponse> response) => _response = response;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/invoices/query/metadata", request.RequestUri!.AbsolutePath);
            Assert.Equal("ACCESS", request.Headers.Authorization?.Parameter);

            var offsetPart = request.RequestUri.Query.TrimStart('?').Split('&')
                .Single(part => part.StartsWith("pageOffset=", StringComparison.Ordinal));
            var offset = int.Parse(offsetPart["pageOffset=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(Calls, offset);
            Calls++;

            var json = JsonSerializer.Serialize(_response(offset), KsefClient.Json);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static KsefDto.InvoiceMetadata Invoice(int index) =>
        new($"KSEF-{index}", $"F/{index}", new DateOnly(2026, 3, 1),
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
            null, 100m, 123m, 23m, "PLN", "VAT");

    private static KsefClient Client(MetadataHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler) { BaseAddress = new Uri("https://ksef.test/v2/") };
        return new KsefClient(http, maxRetries: 0);
    }

    private static Task<List<KsefDto.InvoiceMetadata>> Query(KsefClient client) =>
        client.QueryPurchaseInvoicesAsync("ACCESS",
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Truncated_result_must_not_be_reported_as_complete(bool hasMore)
    {
        using var handler = new MetadataHandler(_ =>
            new KsefDto.QueryResponse(hasMore, true, new List<KsefDto.InvoiceMetadata> { Invoice(0) }));
        var client = Client(handler, out var http);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<KsefException>(() => Query(client));
            Assert.Contains("isTruncated=true", ex.Message);
            Assert.Contains("nie jest kompletny", ex.Message);
        }
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Page_limit_with_more_results_must_not_return_partial_list()
    {
        using var handler = new MetadataHandler(_ =>
            new KsefDto.QueryResponse(true, false, new List<KsefDto.InvoiceMetadata>()));
        var client = Client(handler, out var http);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<KsefException>(() => Query(client));
            Assert.Contains("100 stron", ex.Message);
            Assert.Contains("nie jest kompletny", ex.Message);
        }
        Assert.Equal(100, handler.Calls);
    }

    [Fact]
    public async Task Complete_multi_page_result_returns_all_invoices()
    {
        using var handler = new MetadataHandler(page =>
            new KsefDto.QueryResponse(page == 0, false,
                new List<KsefDto.InvoiceMetadata> { Invoice(page) }));
        var client = Client(handler, out var http);
        using (http)
        {
            var result = await Query(client);
            Assert.Equal(new[] { "KSEF-0", "KSEF-1" }, result.Select(i => i.KsefNumber));
        }
        Assert.Equal(2, handler.Calls);
    }
}
