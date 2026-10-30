using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Jpk;
using PelnaKsiegowosc.Infrastructure.Ksef;
using PelnaKsiegowosc.Infrastructure.Xml;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integrations;

/// <summary>
/// Atrapa serwera KSeF 2.0: sprawdza szyfrowanie tokenu (RSA-OAEP SHA-256), klucza sesji i treści faktury (AES-256-CBC),
/// skróty SHA-256 oraz kolejność wywołań. Odpowiedzi w formacie API v2.
/// </summary>
internal sealed class FakeKsef : HttpMessageHandler
{
    public const string Token = "TEST-TOKEN-1234";
    public const long TimestampMs = 1767225600000;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _certB64;
    private byte[]? _key;
    private byte[]? _iv;
    private int _authPolls;
    public string? ReceivedXml { get; private set; }
    public string? AuthNip { get; private set; }
    public List<string> Calls { get; } = new();
    public int RejectWithCode { get; set; }

    public FakeKsef()
    {
        var req = new CertificateRequest("CN=KSeF test", _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _certB64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));
    }

    private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o, KsefClient.Json), Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.Replace("/v2/", "");
        Calls.Add($"{request.Method} {path}");
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var now = DateTimeOffset.UtcNow;
        switch (request.Method.Method, path)
        {
            case ("GET", "security/public-key-certificates"):
                return Json(new[]
                {
                    new KsefDto.PublicKeyCertificate(_certB64, "C1", "K1", now.AddDays(-1), now.AddYears(1), new List<string> { "KsefTokenEncryption", "SymmetricKeyEncryption" })
                });
            case ("POST", "auth/challenge"):
                return Json(new KsefDto.ChallengeResponse("CH-1", now, TimestampMs, null));
            case ("POST", "auth/ksef-token"):
            {
                var r = JsonSerializer.Deserialize<KsefDto.TokenAuthRequest>(body, KsefClient.Json)!;
                var plain = Encoding.UTF8.GetString(_rsa.Decrypt(Convert.FromBase64String(r.EncryptedToken), RSAEncryptionPadding.OaepSHA256));
                if (plain != $"{Token}|{TimestampMs}" || r.Challenge != "CH-1" || r.PublicKeyId != "K1") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                AuthNip = r.ContextIdentifier.Value;
                return Json(new KsefDto.AuthInitResponse("AUTH-REF", new KsefDto.TokenInfo("AUTH-TOKEN", now.AddMinutes(10))), HttpStatusCode.Accepted);
            }
            case ("GET", "auth/AUTH-REF"):
                if (request.Headers.Authorization?.Parameter != "AUTH-TOKEN") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                return Json(new KsefDto.AuthStatusResponse(now, "Token", new KsefDto.StatusInfo(_authPolls++ == 0 ? 100 : 200, "ok", null)));
            case ("POST", "auth/token/redeem"):
                return Json(new KsefDto.TokensResponse(new KsefDto.TokenInfo("ACCESS", now.AddMinutes(15)), new KsefDto.TokenInfo("REFRESH", now.AddDays(1))));
            case ("POST", "sessions/online"):
            {
                if (request.Headers.Authorization?.Parameter != "ACCESS") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                var r = JsonSerializer.Deserialize<KsefDto.OpenOnlineSessionRequest>(body, KsefClient.Json)!;
                if (r.FormCode.SystemCode != "FA (3)" || r.FormCode.Value != "FA") return new HttpResponseMessage(HttpStatusCode.BadRequest);
                _key = _rsa.Decrypt(Convert.FromBase64String(r.Encryption.EncryptedSymmetricKey), RSAEncryptionPadding.OaepSHA256);
                _iv = Convert.FromBase64String(r.Encryption.InitializationVector);
                return Json(new KsefDto.OpenOnlineSessionResponse("SES-1", now.AddHours(12)), HttpStatusCode.Created);
            }
            case ("POST", "sessions/online/SES-1/invoices"):
            {
                var r = JsonSerializer.Deserialize<KsefDto.SendInvoiceRequest>(body, KsefClient.Json)!;
                var enc = Convert.FromBase64String(r.EncryptedInvoiceContent);
                using var aes = Aes.Create();
                aes.Key = _key!;
                var xml = aes.DecryptCbc(enc, _iv!, PaddingMode.PKCS7);
                if (Convert.ToBase64String(SHA256.HashData(xml)) != r.InvoiceHash || xml.Length != r.InvoiceSize ||
                    Convert.ToBase64String(SHA256.HashData(enc)) != r.EncryptedInvoiceHash) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                ReceivedXml = Encoding.UTF8.GetString(xml);
                return Json(new KsefDto.SendInvoiceResponse("INV-REF"), HttpStatusCode.Accepted);
            }
            case ("GET", "sessions/SES-1/invoices/INV-REF"):
                return RejectWithCode > 0
                    ? Json(new KsefDto.InvoiceStatus(1, null, null, "INV-REF", null, null, new KsefDto.StatusInfo(RejectWithCode, "Błąd weryfikacji semantyki", new List<string> { "P_2 niepoprawne" })))
                    : Json(new KsefDto.InvoiceStatus(1, "FS/1", "5260250274-20260115-0123456789AB-CD", "INV-REF", null, now, new KsefDto.StatusInfo(200, "Sukces", null)));
            case ("POST", "sessions/online/SES-1/close"):
                return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"brak atrapy dla {request.Method} {path}") };
    }
}

public class KsefAndJpkTests
{
    private static XsdValidator Validator() => new(Path.Combine(AppContext.BaseDirectory, "schemas"));

    private static async Task<Document> ApprovedSaleAsync(TestBooks t)
    {
        var cp = await t.AddCounterpartyAsync("K001", "Klient sp. z o.o.", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 15);
        var inv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, cp.Id, d, d,
            new[] { new DocumentLineInput("Usługa doradcza", 2, 1500m, "23", "usł."), new DocumentLineInput("Książka", 1, 100m, "5") }, DueDate: d.AddDays(14)));
        await docs.ApproveAsync(inv.Id);
        return inv;
    }

    [Fact]
    public async Task Fa3_invoice_is_valid_against_bundled_official_schema()
    {
        await using var t = await TestBooks.CreateAsync();
        var inv = await ApprovedSaleAsync(t);
        var (xml, validation) = await new KsefService(t.Db, t.User, t.Clock, Validator()).BuildAsync(inv.Id);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.False(xml.Length >= 3 && xml[0] == 0xEF, "XML nie może zawierać BOM");
        var x = XDocument.Parse(Encoding.UTF8.GetString(xml));
        Assert.Contains(x.Descendants(), e => e.Name.LocalName == "P_15" && e.Value == "3795.00");
    }

    [Fact]
    public async Task Ksef_send_encrypts_token_key_and_invoice_and_stores_ksef_number()
    {
        await using var t = await TestBooks.CreateAsync();
        var inv = await ApprovedSaleAsync(t);
        var fake = new FakeKsef();
        using var http = new HttpClient(fake) { BaseAddress = new Uri("https://ksef.test/v2/") };
        var r = await new KsefService(t.Db, t.User, t.Clock, Validator()).SendAsync(inv.Id, new KsefClient(http, pollDelay: TimeSpan.Zero), FakeKsef.Token);
        Assert.True(r.Accepted, r.Message);
        Assert.Equal(TestBooks.ValidNip, fake.AuthNip);
        Assert.Contains(XDocument.Parse(fake.ReceivedXml!).Descendants(), e => e.Name.LocalName == "P_2" && e.Value == inv.Number);
        var doc = await t.Db.Documents.AsNoTracking().FirstAsync(d => d.Id == inv.Id);
        Assert.Equal(KsefStatus.Przyjeta, doc.KsefStatus);
        Assert.Equal("5260250274-20260115-0123456789AB-CD", doc.KsefNumber);
        Assert.Equal(JpkKsefMarker.NrKSeF, doc.JpkMarker);
        Assert.Contains("POST sessions/online/SES-1/close", fake.Calls);
        var ex = await Assert.ThrowsAsync<PelnaKsiegowosc.Domain.Common.DomainException>(() =>
            new KsefService(t.Db, t.User, t.Clock, Validator()).SendAsync(inv.Id, new KsefClient(http, pollDelay: TimeSpan.Zero), FakeKsef.Token));
        Assert.Contains("ma już numer KSeF", ex.Message);
    }

    [Fact]
    public async Task Ksef_rejection_and_wrong_token_are_reported_without_ksef_number()
    {
        await using var t = await TestBooks.CreateAsync();
        var inv = await ApprovedSaleAsync(t);
        var fake = new FakeKsef { RejectWithCode = 450 };
        using var http = new HttpClient(fake) { BaseAddress = new Uri("https://ksef.test/v2/") };
        var svc = new KsefService(t.Db, t.User, t.Clock, Validator());
        var r = await svc.SendAsync(inv.Id, new KsefClient(http, pollDelay: TimeSpan.Zero), FakeKsef.Token);
        Assert.False(r.Accepted);
        Assert.Contains("450", r.Message);
        var doc = await t.Db.Documents.AsNoTracking().FirstAsync(d => d.Id == inv.Id);
        Assert.Equal(KsefStatus.Odrzucona, doc.KsefStatus);
        Assert.Null(doc.KsefNumber);

        await using var t2 = await TestBooks.CreateAsync();
        var inv2 = await ApprovedSaleAsync(t2);
        using var http2 = new HttpClient(new FakeKsef()) { BaseAddress = new Uri("https://ksef.test/v2/") };
        var ex = await Assert.ThrowsAsync<KsefException>(() => new KsefService(t2.Db, t2.User, t2.Clock, Validator())
            .SendAsync(inv2.Id, new KsefClient(http2, maxRetries: 0, pollDelay: TimeSpan.Zero), "ZŁY-TOKEN"));
        Assert.Equal(401, ex.HttpStatus);
        Assert.Contains(await t2.Db.IntegrationLogs.ToListAsync(), l => !l.Success);
    }

    [Fact]
    public async Task Jpk_v7m_contains_register_rows_and_declaration_matching_the_vat_register()
    {
        await using var t = await TestBooks.CreateAsync();
        var inv = await ApprovedSaleAsync(t);
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        await docs.PostAsync(inv.Id);
        var vat = new VatService(t.Db, t.User, t.Clock);
        var period = new DateOnly(2026, 1, 1);
        var sales = await vat.RegisterAsync(VatRegisterType.Sprzedaz, period);
        var purchases = await vat.RegisterAsync(VatRegisterType.Zakup, period);
        var decl = await vat.ComputeDeclarationAsync(period);
        Assert.Throws<DomainException>(() => JpkV7Builder.Build(t.Company, period, sales, purchases, decl, false, t.Clock.UtcNow, Validator())); // brak e-maila
        t.Company.Email = "biuro@przyklad.pl";
        // plik wytworzony przed 1.02.2026 — struktura (2), bez oznaczeń KSeF
        var r2 = JpkV7Builder.Build(t.Company, period, sales, purchases, decl, false, t.Clock.UtcNow, Validator());
        Assert.Equal(JpkV7Rules.M2.Ns, r2.Document.Root!.Name.Namespace);
        Assert.Empty(r2.Document.Descendants(JpkV7Rules.M2.Ns + "BFK"));
        Assert.True(r2.Validation.IsValid, string.Join(Environment.NewLine, r2.Validation.Errors));
        // wytworzony od 1.02.2026 — struktura (3)
        var r = JpkV7Builder.Build(t.Company, period, sales, purchases, decl, false, new DateTime(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc), Validator());
        var ns = JpkV7Rules.Ns;
        Assert.Single(r.Document.Descendants(ns + "SprzedazWiersz"));
        Assert.Equal("690.00", r.Document.Descendants(ns + "K_20").Single().Value);
        Assert.Equal("5.00", r.Document.Descendants(ns + "K_16").Single().Value); // 5%: K_15/K_16
        Assert.Equal(decl.Payable.ToString("0", System.Globalization.CultureInfo.InvariantCulture), r.Document.Descendants(ns + "P_51").Single().Value);
        Assert.Equal("1", r.Document.Descendants(ns + "BFK").Single().Value); // faktura bez numeru KSeF
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors)); // oficjalny schemat JPK_V7M(3) z crd.gov.pl
    }

    [Fact]
    public async Task Jpk_v7k_has_monthly_records_and_quarterly_declaration_only_in_last_month()
    {
        await using var t = await TestBooks.CreateAsync();
        t.Company.VatPeriod = VatSettlementPeriod.Kwartalny;
        t.Company.Email = "biuro@przyklad.pl";
        await t.Db.SaveChangesAsync();
        var feb10 = new DateTime(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);
        var inv = await ApprovedSaleAsync(t);
        await new DocumentService(t.Db, t.User, t.Clock).PostAsync(inv.Id);
        var vat = new VatService(t.Db, t.User, t.Clock);
        var jan = new DateOnly(2026, 1, 1);
        var mar = new DateOnly(2026, 3, 1);
        Assert.False(await vat.IsDeclarationMonthAsync(jan));
        Assert.True(await vat.IsDeclarationMonthAsync(mar));
        await Assert.ThrowsAsync<DomainException>(() => vat.PostSettlementAsync(jan));

        var janMonth = await vat.ComputeDeclarationAsync(jan, null, monthOnly: true);
        var rJan = JpkV7Builder.Build(t.Company, jan, await vat.RegisterAsync(VatRegisterType.Sprzedaz, jan), await vat.RegisterAsync(VatRegisterType.Zakup, jan), null, janMonth, false, feb10, Validator());
        XNamespace ns = "http://crd.gov.pl/wzor/2025/12/19/14089/";
        Assert.Empty(rJan.Document.Descendants(ns + "Deklaracja"));
        Assert.Single(rJan.Document.Descendants(ns + "SprzedazWiersz"));
        Assert.Equal("JPK_V7K (3)", rJan.Document.Descendants(ns + "KodFormularza").Single().Attribute("kodSystemowy")!.Value);
        Assert.Equal("695.00", rJan.Document.Descendants(ns + "PodatekNalezny").Single().Value);

        var q = await vat.ComputeDeclarationAsync(mar);
        Assert.True(q.Quarterly);
        Assert.Equal(1, q.Quarter);
        Assert.Equal(695m, q.Fields["P_38"]);
        var marMonth = await vat.ComputeDeclarationAsync(mar, null, monthOnly: true);
        var rMar = JpkV7Builder.Build(t.Company, mar, await vat.RegisterAsync(VatRegisterType.Sprzedaz, mar), await vat.RegisterAsync(VatRegisterType.Zakup, mar), q, marMonth, false, feb10.AddMonths(2), Validator());
        Assert.Single(rMar.Document.Descendants(ns + "Deklaracja"));
        Assert.Empty(rMar.Document.Descendants(ns + "SprzedazWiersz"));
        Assert.Equal("695", rMar.Document.Descendants(ns + "P_51").Single().Value);
        Assert.Equal("VAT-7K", rMar.Document.Descendants(ns + "KodFormularzaDekl").Single().Value);
        Assert.True(rJan.Validation.IsValid, string.Join(Environment.NewLine, rJan.Validation.Errors));
        Assert.True(rMar.Validation.IsValid, string.Join(Environment.NewLine, rMar.Validation.Errors));
        await vat.PostSettlementAsync(mar);
    }

    [Fact]
    public async Task Jpk_cit_contains_trial_balance_with_tax_markers_and_journal()
    {
        await using var t = await PelnaKsiegowosc.Tests.Integration.FullYearScenarioTests.BuildFullYearAsync(closeYear: true);
        var fy = await t.Db.FiscalYears.AsNoTracking().SingleAsync(f => f.Id == t.Year.Id);
        var dir = Path.Combine(t.Directory, "schematy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, JpkKrPdBuilder.SchemaFile), """<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="http://jpk.mf.gov.pl/test/kr_pd/"><xsd:element name="Inny"/></xsd:schema>""");
        var r = await JpkKrPdBuilder.BuildAsync(t.Db, t.Company, fy, false, t.Clock.UtcNow, new XsdValidator(dir));
        XNamespace ns = "http://jpk.mf.gov.pl/test/kr_pd/";
        Assert.Equal(ns, r.Document.Root!.Name.Namespace); // przestrzeń nazw z wczytanego schematu
        var zois = r.Document.Descendants(ns + "ZOiSWiersz").ToList();
        var sales = zois.Single(z => z.Element(ns + "KodKonta")!.Value == "700-01");
        Assert.Equal("PD_P", sales.Element(ns + "ZnacznikPD")!.Value);
        Assert.Equal("R.A.I", sales.Element(ns + "ZnacznikRZiS")!.Value);
        var ctrl = r.Document.Descendants(ns + "Ctrl").Single();
        Assert.Equal(ctrl.Element(ns + "SumaWinien")!.Value, ctrl.Element(ns + "SumaMa")!.Value);
        Assert.Equal(ctrl.Element(ns + "SumaWinienZOiS")!.Value, ctrl.Element(ns + "SumaMaZOiS")!.Value);
        Assert.True(r.Document.Descendants(ns + "DziennikWiersz").Count() > 5);
        Assert.False(r.Validation.IsValid); // schemat testowy nie opisuje struktury → plik NIEZWALIDOWANY
    }
}

public class InvoicePdfTests
{
    [Fact]
    public async Task Sales_invoice_pdf_is_generated()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Klient sp. z o.o.", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 15);
        var inv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, cp.Id, d, d,
            new[] { new DocumentLineInput("Usługa doradcza — zażółć gęślą jaźń", 2, 1500m, "23", "usł.") }, DueDate: d.AddDays(14)));
        var full = await docs.GetAsync(inv.Id);
        var pdf = PelnaKsiegowosc.Infrastructure.Export.InvoicePdf.Build(full, t.Company);
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        if (Environment.GetEnvironmentVariable("PK_TEST_PDF_OUT") is { Length: > 0 } outPath) File.WriteAllBytes(outPath, pdf);
    }
}
