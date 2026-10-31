using System.Net;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Tests.Integrations;

public sealed class SchemaOriginTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'/>")
            });
        }
    }

    [Theory]
    [InlineData("https://attacker-gov.pl/schema.xsd", false)]
    [InlineData("https://gov.pl.attacker.example/schema.xsd", false)]
    [InlineData("ftp://crd.gov.pl/schema.xsd", false)]
    [InlineData("https://crd.gov.pl/schema.xsd", true)]
    [InlineData("http://crd.gov.pl/schema.xsd", true)]
    public async Task Download_checks_domain_boundary_and_protocol_before_network_request(string url, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var library = new SchemaLibrary(root, http);
        var schema = new KnownSchema("test", "schema.xsd", url, url, "test");
        try
        {
            if (allowed) Assert.Single(await library.DownloadAsync(schema));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => library.DownloadAsync(schema));
            Assert.Equal(allowed ? 1 : 0, handler.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
