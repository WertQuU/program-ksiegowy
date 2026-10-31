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

    private static async Task<Document> ApprovedSaleAsync(TestBooks t, DateOnly? date = null, string code = "K001")
    {
        var cp = await t.AddCounterpartyAsync(code, "Klient sp. z o.o.", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = date ?? new DateOnly(2026, 1, 15);
        var inv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, cp.Id, d, d,
            new[] { new DocumentLineInput("Usługa doradcza", 2, 1500m, "23", "usł."), new DocumentLineInput("Książka", 1, 100m, "5") }, DueDate: d.AddDays(14)));
        await docs.ApproveAsync(inv.Id);
        return inv;
    }

    [Fact]
    public async Task Installment_invoice_moves_due_date_with_payments_and_stays_valid_in_ksef()
    {
        await using var t = await TestBooks.CreateAsync();
        var inv = await ApprovedSaleAsync(t); // brutto 3795,00
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => docs.SetInstallmentsAsync(inv.Id, new[] { (inv.IssueDate.AddDays(10), 1000m), (inv.IssueDate.AddDays(40), 1000m) }));
        var r1 = inv.IssueDate.AddDays(14); var r2 = inv.IssueDate.AddDays(44); var r3 = inv.IssueDate.AddDays(74);
        await docs.SetInstallmentsAsync(inv.Id, new[] { (r1, 1265m), (r2, 1265m), (r3, 1265m) });
        var (xml, validation) = await new KsefService(t.Db, t.User, t.Clock, Validator()).BuildAsync(inv.Id);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(3, XDocument.Parse(Encoding.UTF8.GetString(xml)).Descendants().Count(e => e.Name.LocalName == "TerminPlatnosci"));

        await docs.PostAsync(inv.Id);
        var item = await t.Db.OpenItems.AsNoTracking().SingleAsync(o => o.DocumentId == inv.Id);
        Assert.Equal(r1, item.DueDate);
        // wpłata pierwszej raty i części drugiej — termin przechodzi na ratę 2, po spłacie 2 rat — na ratę 3
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(r1, "WB/1", new PelnaKsiegowosc.Application.Ledger.LineInput("130-01", 1500m, 0),
            new PelnaKsiegowosc.Application.Ledger.LineInput(item.AccountNumber, 0, 1500m, item.CounterpartyId, OpenItemId: item.Id)));
        await docs.RefreshPaymentStatusAsync(inv.Id);
        Assert.Equal(r2, (await t.Db.OpenItems.AsNoTracking().SingleAsync(o => o.Id == item.Id)).DueDate);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(r2, "WB/2", new PelnaKsiegowosc.Application.Ledger.LineInput("130-01", 1030m, 0),
            new PelnaKsiegowosc.Application.Ledger.LineInput(item.AccountNumber, 0, 1030m, item.CounterpartyId, OpenItemId: item.Id)));
        await docs.RefreshPaymentStatusAsync(inv.Id);
        Assert.Equal(r3, (await t.Db.OpenItems.AsNoTracking().SingleAsync(o => o.Id == item.Id)).DueDate);
        Assert.Equal(PaymentStatus.CzesciowoOplacony, (await docs.GetAsync(inv.Id)).PaymentStatus);
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
        // okres styczeń 2026 — zawsze struktura (2), także gdy plik (np. korekta) powstaje po 1.02.2026; bez oznaczeń KSeF
        foreach (var generated in new[] { t.Clock.UtcNow, new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc) })
        {
            var r2 = JpkV7Builder.Build(t.Company, period, sales, purchases, decl, false, generated, Validator());
            Assert.Equal(JpkV7Rules.M2.Ns, r2.Document.Root!.Name.Namespace);
            Assert.Empty(r2.Document.Descendants(JpkV7Rules.M2.Ns + "BFK"));
            Assert.True(r2.Validation.IsValid, string.Join(Environment.NewLine, r2.Validation.Errors));
            Assert.Equal("690.00", r2.Document.Descendants(JpkV7Rules.M2.Ns + "K_20").Single().Value);
        }
        // okres luty 2026 — struktura (3) z oznaczeniami KSeF
        var feb = new DateOnly(2026, 2, 1);
        var invFeb = await ApprovedSaleAsync(t, new DateOnly(2026, 2, 16), "K002");
        await docs.PostAsync(invFeb.Id);
        var declFeb = await vat.ComputeDeclarationAsync(feb);
        var r = JpkV7Builder.Build(t.Company, feb, await vat.RegisterAsync(VatRegisterType.Sprzedaz, feb), await vat.RegisterAsync(VatRegisterType.Zakup, feb), declFeb, false,
            new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), Validator());
        var ns = JpkV7Rules.Ns;
        Assert.Equal(ns, r.Document.Root!.Name.Namespace);
        Assert.Single(r.Document.Descendants(ns + "SprzedazWiersz"));
        Assert.Equal("690.00", r.Document.Descendants(ns + "K_20").Single().Value);
        Assert.Equal("5.00", r.Document.Descendants(ns + "K_16").Single().Value); // 5%: K_15/K_16
        Assert.Equal(declFeb.Payable.ToString("0", System.Globalization.CultureInfo.InvariantCulture), r.Document.Descendants(ns + "P_51").Single().Value);
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
        var nsJan = JpkV7Rules.K2.Ns; // za styczeń 2026 — struktura (2)
        Assert.Empty(rJan.Document.Descendants(nsJan + "Deklaracja"));
        Assert.Single(rJan.Document.Descendants(nsJan + "SprzedazWiersz"));
        Assert.Equal("JPK_V7K (2)", rJan.Document.Descendants(nsJan + "KodFormularza").Single().Attribute("kodSystemowy")!.Value);
        Assert.Equal("695.00", rJan.Document.Descendants(nsJan + "PodatekNalezny").Single().Value);
        XNamespace ns = "http://crd.gov.pl/wzor/2025/12/19/14089/";

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
    public async Task Jpk_cit_reports_multi_level_analytics_with_parent_chain_and_aggregated_turnover()
    {
        await using var t = await TestBooks.CreateAsync();
        var chart = new PelnaKsiegowosc.Application.Ledger.ChartService(t.Db, t.User, t.Clock);
        await chart.AddAccountAsync(t.Chart.Id, "700-02-01", "Sprzedaż usług — klient A", Domain.Accounts.AccountType.WynikowePrzychodowe, profitLossLine: "R.A.I", cit: Domain.Accounts.CitClassification.PrzychodPodatkowy);
        await chart.AddAccountAsync(t.Chart.Id, "700-02-02", "Sprzedaż usług — klient B", Domain.Accounts.AccountType.WynikowePrzychodowe, profitLossLine: "R.A.I", cit: Domain.Accounts.CitClassification.PrzychodPodatkowy);
        var d = new DateOnly(2026, 2, 10);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "S/1", new("130-01", 300m, 0), new("700-02-01", 0, 300m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "S/2", new("130-01", 200m, 0), new("700-02-02", 0, 200m)));
        await t.Db.SaveChangesAsync();
        t.Company.Voivodeship = "mazowieckie"; t.Company.County = "Warszawa"; t.Company.Commune = "Warszawa"; t.Company.TaxOfficeCode = "1471";
        var rec = await new PelnaKsiegowosc.Application.Cit.CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        var r = await JpkKrPdBuilder.BuildAsync(t.Db, t.Company, t.Year, rec.Rpd, false, t.Clock.UtcNow, Validator());
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors.Take(10)));
        var ns = JpkKrPdBuilder.Ns;
        var zois = r.Document.Descendants(ns + "ZOiS7").ToDictionary(z => z.Element(ns + "S_1")!.Value);
        decimal Ma(string acc) => decimal.Parse(zois[acc].Element(ns + "S_7")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("700-02", zois["700-02-01"].Element(ns + "S_3")!.Value);
        Assert.Equal("700", zois["700-02"].Element(ns + "S_3")!.Value);
        Assert.Equal("700", zois["700"].Element(ns + "S_3")!.Value);
        Assert.Equal(500m, Ma("700-02"));
        Assert.Equal(Ma("700-02-01") + Ma("700-02-02"), Ma("700-02"));
        Assert.Equal(500m, Ma("700"));
        Assert.Equal("130", zois["130-01"].Element(ns + "S_3")!.Value);
        Assert.True(zois.ContainsKey("130"));
    }

    [Fact]
    public async Task Jpk_cit_refuses_ambiguous_d3_for_journal_entry_with_two_tax_identified_counterparties()
    {
        await using var t = await TestBooks.CreateAsync();
        var a = await t.AddCounterpartyAsync("KFA", "Firma A", "7740001454");
        var b = await t.AddCounterpartyAsync("KFB", "Firma B", "5260250274");
        var date = new DateOnly(2026, 2, 10);
        // Jeden zbiorczy dekret, dwie różne osoby prawne z NIP: pojedyncze
        // pole D_3 nie może wskazywać arbitralnie pierwszej analityki.
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(date, "PK/ZBIORCZY/1",
            new("130-01", 300m, 0),
            new("200", 0, 150m, a.Id),
            new("200", 0, 150m, b.Id)));
        t.Company.Voivodeship = "mazowieckie";
        t.Company.County = "Warszawa";
        t.Company.Commune = "Warszawa";
        t.Company.TaxOfficeCode = "1471";
        var cit = await new PelnaKsiegowosc.Application.Cit.CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            JpkKrPdBuilder.BuildAsync(t.Db, t.Company, t.Year, cit.Rpd, false, t.Clock.UtcNow, Validator()));
        Assert.Contains("D_3", exception.Message);
        Assert.Contains("kontrahentów", exception.Message);
    }

    [Fact]
    public async Task Jpk_cit_reports_every_tax_id_set_of_a_counterparty_used_during_the_year()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("KZM", "Klient zmieniający NIP", "7740001454");
        await PelnaKsiegowosc.Tests.Modules.SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, cp.Id, 100m, new DateOnly(2026, 2, 10));
        cp.Nip = "5260250274";                                                                   // zmiana identyfikatora w trakcie roku
        await t.Db.SaveChangesAsync();
        await PelnaKsiegowosc.Tests.Modules.SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, cp.Id, 200m, new DateOnly(2026, 6, 10));
        t.Company.Voivodeship = "mazowieckie"; t.Company.County = "Warszawa"; t.Company.Commune = "Warszawa"; t.Company.TaxOfficeCode = "1471";
        var rec = await new PelnaKsiegowosc.Application.Cit.CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        var r = await JpkKrPdBuilder.BuildAsync(t.Db, t.Company, t.Year, rec.Rpd, false, t.Clock.UtcNow, Validator());
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors.Take(10)));
        var ns = JpkKrPdBuilder.Ns;
        var sets = r.Document.Descendants(ns + "Kontrahent").ToDictionary(k => k.Element(ns + "T_1")!.Value, k => k.Element(ns + "T_3")!.Value);
        Assert.Equal("7740001454", sets["KZM"]);
        Assert.Equal("5260250274", sets["KZM/2"]);
        var d3 = r.Document.Descendants(ns + "Dziennik").Select(x => x.Element(ns + "D_3")?.Value).Where(v => v is not null).Distinct().ToList();
        Assert.Contains("KZM", d3);
        Assert.Contains("KZM/2", d3);
    }

    [Fact]
    public async Task Jpk_cit_assigns_identity_valid_on_operation_date_to_manual_entries_after_nip_change()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("KRE", "Kontrahent ręczny", "7740001454");
        // zapis ręczny (bez dokumentu) w lutym — przy starym NIP
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 2, 10), "PK/1", new("130-01", 100m, 0), new("200", 0, 100m, cp.Id)));
        await t.Db.SaveChangesAsync();
        cp.Nip = "5260250274";
        cp.IdentityValidFrom = new DateOnly(2026, 5, 1);                                        // nowy NIP od 1 maja
        await t.Db.SaveChangesAsync();
        Assert.Equal(new DateOnly(2026, 4, 30), (await t.Db.CounterpartyIdentityChanges.AsNoTracking().SingleAsync()).ValidUntil);
        // zapisy ręczne: kwietniowy wprowadzony po zmianie (dane sprzed zmiany) i czerwcowy (nowe dane)
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 4, 20), "PK/2", new("130-01", 50m, 0), new("200", 0, 50m, cp.Id)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 6, 10), "PK/3", new("130-01", 70m, 0), new("200", 0, 70m, cp.Id)));
        await t.Db.SaveChangesAsync();
        t.Company.Voivodeship = "mazowieckie"; t.Company.County = "Warszawa"; t.Company.Commune = "Warszawa"; t.Company.TaxOfficeCode = "1471";
        var rec = await new PelnaKsiegowosc.Application.Cit.CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        var r = await JpkKrPdBuilder.BuildAsync(t.Db, t.Company, t.Year, rec.Rpd, false, t.Clock.UtcNow, Validator());
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors.Take(10)));
        var ns = JpkKrPdBuilder.Ns;
        var sets = r.Document.Descendants(ns + "Kontrahent").ToDictionary(k => k.Element(ns + "T_1")!.Value, k => k.Element(ns + "T_3")!.Value);
        Assert.Equal("7740001454", sets["KRE"]);
        Assert.Equal("5260250274", sets["KRE/2"]);
        string? RefOf(string doc) => r.Document.Descendants(ns + "Dziennik").First(x => x.Elements().Any(el => el.Value == doc)).Element(ns + "D_3")?.Value;
        Assert.Equal("KRE", RefOf("PK/1"));
        Assert.Equal("KRE", RefOf("PK/2"));
        Assert.Equal("KRE/2", RefOf("PK/3"));
        // kolejna zmiana bez podanej daty — poprzednia data obowiązywania nie jest ponownie użyta
        cp.Nip = "7740001454";
        await t.Db.SaveChangesAsync();
        Assert.Equal(2, await t.Db.CounterpartyIdentityChanges.CountAsync());
        Assert.NotEqual(new DateOnly(2026, 5, 1), cp.IdentityValidFrom);
    }

    [Fact]
    public async Task Jpk_cit_lists_only_counterparties_with_tax_id()
    {
        await using var t = await TestBooks.CreateAsync();
        var company = await t.AddCounterpartyAsync("K-FIRMA", "Firma sp. z o.o.", "7740001454");
        var person = await t.AddCounterpartyAsync("K-OSOBA", "Jan Kowalski (konsument)");
        var d = new DateOnly(2026, 2, 10);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "S/1", new("130-01", 300m, 0, company.Id), new("700-01", 0, 300m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "S/2", new("130-01", 200m, 0, person.Id), new("700-01", 0, 200m)));
        await t.Db.SaveChangesAsync();
        t.Company.Voivodeship = "mazowieckie"; t.Company.County = "Warszawa"; t.Company.Commune = "Warszawa"; t.Company.TaxOfficeCode = "1471";
        var rec = await new PelnaKsiegowosc.Application.Cit.CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        var r = await JpkKrPdBuilder.BuildAsync(t.Db, t.Company, t.Year, rec.Rpd, false, t.Clock.UtcNow, Validator());
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors.Take(10)));
        var ns = JpkKrPdBuilder.Ns;
        var codes = r.Document.Descendants(ns + "Kontrahent").Select(k => k.Element(ns + "T_1")!.Value).ToList();
        Assert.Contains("K-FIRMA", codes);
        Assert.DoesNotContain("K-OSOBA", codes);
        var refs = r.Document.Descendants(ns + "Dziennik").Select(x => x.Element(ns + "D_3")?.Value).Where(v => v is not null).ToList();
        Assert.All(refs, v => Assert.Contains(v, codes));                                    // każde D_3 wskazuje kontrahenta z węzła
    }

    [Fact]
    public async Task Jpk_cit_is_valid_against_official_jpk_kr_pd_schema()
    {
        await using var t = await PelnaKsiegowosc.Tests.Integration.FullYearScenarioTests.BuildFullYearAsync(closeYear: true);
        var fy = await t.Db.FiscalYears.AsNoTracking().SingleAsync(f => f.Id == t.Year.Id);
        var rec = await new PelnaKsiegowosc.Application.Cit.CitService(t.Db, t.User, t.Clock).ReconcileAsync(fy.Id, 12);
        // RPD z uzgodnienia: dochód = wynik brutto − K1 − K2 + K3 + K4 + K5 − K6 + K7 − K8
        Assert.Equal(rec.TaxRevenue - rec.TaxCosts, rec.Rpd.TaxIncomeFrom(rec.BookResultBeforeTax));
        await Assert.ThrowsAsync<DomainException>(() => JpkKrPdBuilder.BuildAsync(t.Db, t.Company, fy, rec.Rpd, false, t.Clock.UtcNow, Validator())); // brak powiatu/gminy
        t.Company.Voivodeship = "mazowieckie";
        t.Company.County = "Warszawa";
        t.Company.Commune = "Warszawa";
        var r = await JpkKrPdBuilder.BuildAsync(t.Db, t.Company, fy, rec.Rpd, false, t.Clock.UtcNow, Validator());
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors.Take(15)));
        var ns = JpkKrPdBuilder.Ns;
        var zois = r.Document.Descendants(ns + "ZOiS7").ToList();
        string Marker(string acc) => zois.Single(z => z.Element(ns + "S_1")!.Value == acc).Element(ns + "S_12_1")!.Value;
        Assert.Equal("RPAI_RKAI_POZ", Marker("700-01"));
        Assert.Equal("BABIII1c1_SPKR", Marker("130-01"));
        Assert.Equal("BPAI", Marker("801"));
        Assert.Equal("BAAII1c_W", Marker("010-03"));
        Assert.Equal("BAAII1c_U", Marker("070-03"));
        // ZOiS: konta syntetyczne i analityczne z obrotami lub saldami (wyjaśnienia MF); syntetyka: S_3 = własny numer,
        // obroty = suma analityk; analityka: S_3 = konto nadrzędne
        var z700 = zois.Single(z => z.Element(ns + "S_1")!.Value == "700");
        var z70001 = zois.Single(z => z.Element(ns + "S_1")!.Value == "700-01");
        Assert.Equal("700", z700.Element(ns + "S_3")!.Value);
        Assert.Equal("700", z70001.Element(ns + "S_3")!.Value);
        var analytics700 = zois.Where(z => z.Element(ns + "S_3")!.Value == "700" && z.Element(ns + "S_1")!.Value != "700").ToList();
        Assert.Equal(analytics700.Sum(z => decimal.Parse(z.Element(ns + "S_7")!.Value, System.Globalization.CultureInfo.InvariantCulture)),
            decimal.Parse(z700.Element(ns + "S_7")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("RPAI_RKAI_POZ", z700.Element(ns + "S_12_1")!.Value);                 // jednolity znacznik analityk
        Assert.Equal("801", zois.Single(z => z.Element(ns + "S_1")!.Value == "801").Element(ns + "S_3")!.Value); // konto bez analityki
        Assert.Equal(zois.Count, zois.Select(z => z.Element(ns + "S_1")!.Value).Distinct().Count()); // każde konto raz
        var ctrl = r.Document.Descendants(ns + "Ctrl").Single();
        Assert.Equal(ctrl.Element(ns + "C_4")!.Value, ctrl.Element(ns + "C_5")!.Value);
        Assert.True(r.Document.Descendants(ns + "Dziennik").Count() > 5);
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
