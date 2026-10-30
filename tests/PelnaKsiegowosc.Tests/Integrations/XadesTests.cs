using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using PelnaKsiegowosc.Infrastructure.Ksef;

namespace PelnaKsiegowosc.Tests.Integrations;

public class XadesTests
{
    private static X509Certificate2 TestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Jan Kowalski, SERIALNUMBER=TINPL-5260250274, G=Jan, SN=Kowalski, C=PL", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x");
    }

    [Fact]
    public void Auth_token_request_is_signed_with_xades_bes_and_tampering_is_detected()
    {
        using var cert = TestCertificate();
        var doc = XadesSigner.AuthTokenRequest("20250604-CR-461EA5B000-537A6BA15D-D7", "5260250274", false);
        var signed = XadesSigner.Sign(doc, cert, DateTimeOffset.UtcNow);
        Assert.True(XadesSigner.Verify(signed));
        var ns = new XmlNamespaceManager(signed.NameTable);
        ns.AddNamespace("x", XadesSigner.XadesNs);
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
        ns.AddNamespace("a", XadesSigner.AuthTokenNs);
        Assert.NotNull(signed.SelectSingleNode("//x:SignedProperties/x:SignedSignatureProperties/x:SigningCertificate", ns));
        Assert.Equal("http://www.w3.org/2001/04/xmldsig-more#rsa-sha256", signed.SelectSingleNode("//ds:SignatureMethod/@Algorithm", ns)!.Value);
        Assert.Equal(2, signed.SelectNodes("//ds:SignedInfo/ds:Reference", ns)!.Count);

        signed.SelectSingleNode("//a:Nip", ns)!.InnerText = "1111111111";
        Assert.False(XadesSigner.Verify(signed));
    }

    private sealed class FakeAuth : HttpMessageHandler
    {
        public string? SignedXml;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath.TrimStart('/');
            string body = path switch
            {
                "auth/challenge" => """{"challenge":"CH-1","timestamp":"2026-10-30T10:00:00Z","timestampMs":1}""",
                "auth/xades-signature" => """{"referenceNumber":"REF-1","authenticationToken":{"token":"tmp","validUntil":"2026-10-30T11:00:00Z"}}""",
                "auth/REF-1" => """{"startDate":"2026-10-30T10:00:00Z","authenticationMethod":"QualifiedSignature","status":{"code":200,"description":"OK"}}""",
                "auth/token/redeem" => """{"accessToken":{"token":"acc","validUntil":"2026-10-30T11:00:00Z"},"refreshToken":{"token":"ref","validUntil":"2026-11-30T11:00:00Z"}}""",
                _ => ""
            };
            if (path == "auth/xades-signature")
            {
                Assert.Equal("application/xml", r.Content!.Headers.ContentType!.MediaType);
                SignedXml = await r.Content.ReadAsStringAsync(ct);
            }
            return new HttpResponseMessage(body.Length == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Ksef_authentication_with_certificate_sends_signed_request_and_redeems_tokens()
    {
        using var cert = TestCertificate();
        var fake = new FakeAuth();
        var client = new KsefClient(new HttpClient(fake) { BaseAddress = new Uri("https://ksef.test/") }, pollDelay: TimeSpan.Zero);
        var tokens = await new KsefCredentials(null, cert).AuthenticateAsync(client, "5260250274", default);
        Assert.Equal("acc", tokens.AccessToken.Token);
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(fake.SignedXml!);
        Assert.True(XadesSigner.Verify(doc));
        Assert.Contains("<Challenge>CH-1</Challenge>", fake.SignedXml);
        Assert.Contains("certificateSubject", fake.SignedXml);
    }
}
