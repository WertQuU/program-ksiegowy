using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Configuration;

namespace PelnaKsiegowosc.Infrastructure.Ksef;

public sealed class KsefException : DomainException
{
    public KsefException(string message, int? httpStatus = null, int? ksefCode = null) : base(message)
    {
        HttpStatus = httpStatus;
        KsefCode = ksefCode;
    }

    public int? HttpStatus { get; }
    public int? KsefCode { get; }
}

/// <summary>Kontrakty API KSeF 2.0 (zgodne z oficjalną specyfikacją OpenAPI v2, repozytorium CIRFMF/ksef-docs).</summary>
public static class KsefDto
{
    public sealed record TokenInfo(string Token, DateTimeOffset ValidUntil);
    public sealed record StatusInfo(int Code, string Description, List<string>? Details);
    public sealed record ChallengeResponse(string Challenge, DateTimeOffset Timestamp, long TimestampMs, string? ClientIp);
    public sealed record ContextIdentifier(string Type, string Value);
    public sealed record TokenAuthRequest(string Challenge, ContextIdentifier ContextIdentifier, string EncryptedToken, string? PublicKeyId);
    public sealed record AuthInitResponse(string ReferenceNumber, TokenInfo AuthenticationToken);
    public sealed record AuthStatusResponse(DateTimeOffset StartDate, string? AuthenticationMethod, StatusInfo Status);
    public sealed record TokensResponse(TokenInfo AccessToken, TokenInfo RefreshToken);
    public sealed record RefreshResponse(TokenInfo AccessToken);
    public sealed record PublicKeyCertificate(string Certificate, string CertificateId, string PublicKeyId, DateTimeOffset ValidFrom, DateTimeOffset ValidTo, List<string> Usage);
    public sealed record FormCode(string SystemCode, string SchemaVersion, string Value);
    public sealed record EncryptionInfo(string EncryptedSymmetricKey, string InitializationVector, string? PublicKeyId);
    public sealed record OpenOnlineSessionRequest(FormCode FormCode, EncryptionInfo Encryption);
    public sealed record OpenOnlineSessionResponse(string ReferenceNumber, DateTimeOffset ValidUntil);
    public sealed record SendInvoiceRequest(string InvoiceHash, long InvoiceSize, string EncryptedInvoiceHash, long EncryptedInvoiceSize, string EncryptedInvoiceContent, bool? OfflineMode);
    public sealed record SendInvoiceResponse(string ReferenceNumber);
    public sealed record InvoiceStatus(int OrdinalNumber, string? InvoiceNumber, string? KsefNumber, string ReferenceNumber, string? InvoiceHash, DateTimeOffset? AcquisitionDate, StatusInfo Status);
    public sealed record SessionStatus(StatusInfo Status, int? InvoiceCount, int? SuccessfulInvoiceCount, int? FailedInvoiceCount);
    public sealed record DateRange(string DateType, DateTimeOffset From, DateTimeOffset? To);
    public sealed record QueryFilters(string SubjectType, DateRange DateRange);
    public sealed record Party(string? Nip, string? Name);
    public sealed record InvoiceMetadata(string KsefNumber, string InvoiceNumber, DateOnly IssueDate, DateTimeOffset InvoicingDate, Party? Seller,
        decimal NetAmount, decimal GrossAmount, decimal VatAmount, string Currency, string? InvoiceType);
    public sealed record QueryResponse(bool HasMore, bool IsTruncated, List<InvoiceMetadata> Invoices);
}

/// <summary>
/// Klient API KSeF 2.0: uwierzytelnianie tokenem KSeF (RSA-OAEP SHA-256), sesja interaktywna (AES-256-CBC/PKCS#7, klucz
/// szyfrowany RSA-OAEP SHA-256 kluczem publicznym MF), wysyłka i status faktur, pobieranie faktur zakupowych.
/// Ponawianie przy 429/5xx z respektowaniem nagłówka Retry-After. Tokeny nigdy nie są logowane.
/// Uwierzytelnianie podpisem XAdES (kwalifikowanym / Profilem Zaufanym) — NIEZAIMPLEMENTOWANE.
/// </summary>
/// <summary>Poświadczenia KSeF: token albo certyfikat (podpis XAdES). Certyfikat może zawierać NIP/PESEL albo być identyfikowany odciskiem.</summary>
public sealed record KsefCredentials(string? Token, System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate, bool ByFingerprint = false)
{
    public static KsefCredentials FromToken(string token) => new(token, null);

    public Task<KsefDto.TokensResponse> AuthenticateAsync(KsefClient client, string nip, CancellationToken ct) =>
        Certificate is not null ? client.AuthenticateWithCertificateAsync(nip, Certificate, ByFingerprint, ct)
        : client.AuthenticateWithTokenAsync(nip, Token ?? throw new KsefException("Brak tokena ani certyfikatu KSeF."), ct);
}

public sealed class KsefClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static Uri BaseUrl(KsefEnvironment env) => env switch
    {
        KsefEnvironment.Test => new Uri("https://api-test.ksef.mf.gov.pl/v2/"),
        KsefEnvironment.Demo => new Uri("https://api-demo.ksef.mf.gov.pl/v2/"),
        _ => new Uri("https://api.ksef.mf.gov.pl/v2/")
    };

    private readonly HttpClient _http;
    private readonly Action<string, bool, int?, string?>? _log;
    private readonly int _maxRetries;
    private readonly TimeSpan _pollDelay;

    public KsefClient(HttpClient http, Action<string, bool, int?, string?>? log = null, int maxRetries = 4, TimeSpan? pollDelay = null)
    {
        _http = http;
        _log = log;
        _maxRetries = maxRetries;
        _pollDelay = pollDelay ?? TimeSpan.FromSeconds(1);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<T> SendAsync<T>(string operation, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            req.Headers.Add("X-Error-Format", "problem-details");
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                _log?.Invoke(operation, false, null, "błąd sieci, ponawianie: " + ex.Message);
                await Task.Delay(Backoff(attempt, null), ct);
                continue;
            }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    _log?.Invoke(operation, true, (int)resp.StatusCode, null);
                    if (typeof(T) == typeof(string)) return (T)(object)body;
                    return JsonSerializer.Deserialize<T>(body, Json) ?? throw new KsefException($"{operation}: pusta odpowiedź.", (int)resp.StatusCode);
                }
                var retriable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                _log?.Invoke(operation, false, (int)resp.StatusCode, Trunc(body, 500));
                if (retriable && attempt < _maxRetries)
                {
                    await Task.Delay(Backoff(attempt, resp.Headers.RetryAfter), ct);
                    continue;
                }
                throw new KsefException($"KSeF — {operation}: HTTP {(int)resp.StatusCode}. {Trunc(body, 800)}", (int)resp.StatusCode);
            }
        }
    }

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } d) return d > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : d;
        return TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, attempt)));
    }

    private static HttpRequestMessage Req(HttpMethod m, string path, string? bearer = null, object? body = null)
    {
        var r = new HttpRequestMessage(m, path);
        if (bearer is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) r.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return r;
    }

    public Task<List<KsefDto.PublicKeyCertificate>> GetPublicKeysAsync(CancellationToken ct = default) =>
        SendAsync<List<KsefDto.PublicKeyCertificate>>("pobranie kluczy publicznych", () => Req(HttpMethod.Get, "security/public-key-certificates"), ct);

    public static (RSA rsa, string publicKeyId) SelectKey(IEnumerable<KsefDto.PublicKeyCertificate> keys, string usage, DateTimeOffset now)
    {
        var k = keys.Where(x => x.Usage.Contains(usage) && x.ValidFrom <= now && x.ValidTo >= now).OrderByDescending(x => x.ValidFrom).FirstOrDefault()
                ?? throw new KsefException($"Brak ważnego klucza publicznego KSeF o przeznaczeniu {usage}.");
        var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(k.Certificate));
        return (cert.GetRSAPublicKey() ?? throw new KsefException("Klucz publiczny KSeF nie jest kluczem RSA (ECDSA nieobsługiwane)."), k.PublicKeyId);
    }

    /// <summary>Uwierzytelnienie tokenem KSeF: challenge → szyfrowanie „token|timestampMs” RSA-OAEP SHA-256 → status → redeem.</summary>
    public async Task<KsefDto.TokensResponse> AuthenticateWithTokenAsync(string nip, string ksefToken, CancellationToken ct = default)
    {
        var keys = await GetPublicKeysAsync(ct);
        var (rsa, keyId) = SelectKey(keys, "KsefTokenEncryption", DateTimeOffset.UtcNow);
        var challenge = await SendAsync<KsefDto.ChallengeResponse>("wyzwanie uwierzytelnienia", () => Req(HttpMethod.Post, "auth/challenge"), ct);
        var payload = Encoding.UTF8.GetBytes($"{ksefToken}|{challenge.TimestampMs}");
        var encrypted = Convert.ToBase64String(rsa.Encrypt(payload, RSAEncryptionPadding.OaepSHA256));
        CryptographicOperations.ZeroMemory(payload);
        var init = await SendAsync<KsefDto.AuthInitResponse>("uwierzytelnienie tokenem", () => Req(HttpMethod.Post, "auth/ksef-token", null,
            new KsefDto.TokenAuthRequest(challenge.Challenge, new KsefDto.ContextIdentifier("Nip", nip), encrypted, keyId)), ct);
        return await CompleteAuthAsync(init, ct);
    }

    private async Task<KsefDto.TokensResponse> CompleteAuthAsync(KsefDto.AuthInitResponse init, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            var st = await SendAsync<KsefDto.AuthStatusResponse>("status uwierzytelnienia", () => Req(HttpMethod.Get, $"auth/{init.ReferenceNumber}", init.AuthenticationToken.Token), ct);
            if (st.Status.Code == 200) break;
            if (st.Status.Code >= 300)
                throw new KsefException($"Uwierzytelnienie w KSeF nieudane ({st.Status.Code}): {st.Status.Description} {string.Join("; ", st.Status.Details ?? new())}", null, st.Status.Code);
            await Task.Delay(_pollDelay, ct);
            if (i == 59) throw new KsefException("Przekroczono czas oczekiwania na uwierzytelnienie w KSeF.");
        }
        return await SendAsync<KsefDto.TokensResponse>("pobranie tokenów dostępowych", () => Req(HttpMethod.Post, "auth/token/redeem", init.AuthenticationToken.Token), ct);
    }

    /// <summary>
    /// Uwierzytelnienie podpisem XAdES: challenge → AuthTokenRequest podpisany certyfikatem (kwalifikowany podpis/pieczęć,
    /// certyfikat KSeF) → POST auth/xades-signature → status → redeem.
    /// </summary>
    public async Task<KsefDto.TokensResponse> AuthenticateWithCertificateAsync(string nip, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        bool byFingerprint = false, CancellationToken ct = default)
    {
        var challenge = await SendAsync<KsefDto.ChallengeResponse>("wyzwanie uwierzytelnienia", () => Req(HttpMethod.Post, "auth/challenge"), ct);
        var signed = XadesSigner.Sign(XadesSigner.AuthTokenRequest(challenge.Challenge, nip, byFingerprint), certificate, DateTimeOffset.UtcNow);
        if (!XadesSigner.Verify(signed)) throw new KsefException("Podpis XAdES nie przeszedł kontroli własnej — sprawdź certyfikat.");
        var xml = signed.OuterXml;
        var init = await SendAsync<KsefDto.AuthInitResponse>("uwierzytelnienie podpisem XAdES", () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "auth/xades-signature") { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            return r;
        }, ct);
        return await CompleteAuthAsync(init, ct);
    }

    public Task<KsefDto.RefreshResponse> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        SendAsync<KsefDto.RefreshResponse>("odświeżenie tokena", () => Req(HttpMethod.Post, "auth/token/refresh", refreshToken), ct);

    /// <summary>Sesja interaktywna z nowym kluczem AES-256 (zalecane: nowy klucz dla każdej sesji).</summary>
    public async Task<OnlineSession> OpenOnlineSessionAsync(string accessToken, CancellationToken ct = default)
    {
        var keys = await GetPublicKeysAsync(ct);
        var (rsa, keyId) = SelectKey(keys, "SymmetricKeyEncryption", DateTimeOffset.UtcNow);
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        var encKey = Convert.ToBase64String(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256));
        var resp = await SendAsync<KsefDto.OpenOnlineSessionResponse>("otwarcie sesji interaktywnej", () => Req(HttpMethod.Post, "sessions/online", accessToken,
            new KsefDto.OpenOnlineSessionRequest(new KsefDto.FormCode(Fa3InvoiceBuilder.SystemCode, Fa3InvoiceBuilder.SchemaVersion, Fa3InvoiceBuilder.FormValue),
                new KsefDto.EncryptionInfo(encKey, Convert.ToBase64String(iv), keyId))), ct);
        return new OnlineSession(resp.ReferenceNumber, resp.ValidUntil, key, iv, accessToken);
    }

    public sealed class OnlineSession : IDisposable
    {
        internal OnlineSession(string referenceNumber, DateTimeOffset validUntil, byte[] key, byte[] iv, string accessToken)
        {
            ReferenceNumber = referenceNumber;
            ValidUntil = validUntil;
            Key = key;
            Iv = iv;
            AccessToken = accessToken;
        }

        public string ReferenceNumber { get; }
        public DateTimeOffset ValidUntil { get; }
        internal byte[] Key { get; }
        internal byte[] Iv { get; }
        internal string AccessToken { get; }
        public void Dispose() => CryptographicOperations.ZeroMemory(Key);
    }

    public static byte[] EncryptInvoice(byte[] xml, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(xml, iv, PaddingMode.PKCS7);
    }

    public Task<KsefDto.SendInvoiceResponse> SendInvoiceAsync(OnlineSession s, byte[] invoiceXml, CancellationToken ct = default)
    {
        if (invoiceXml.Length > 1_000_000) throw new KsefException("Faktura przekracza 1 MB (limit KSeF dla faktury bez załączników).");
        if (invoiceXml.Length >= 3 && invoiceXml[0] == 0xEF && invoiceXml[1] == 0xBB && invoiceXml[2] == 0xBF) throw new KsefException("Faktura nie może zawierać znaku BOM.");
        var enc = EncryptInvoice(invoiceXml, s.Key, s.Iv);
        var body = new KsefDto.SendInvoiceRequest(Convert.ToBase64String(SHA256.HashData(invoiceXml)), invoiceXml.Length,
            Convert.ToBase64String(SHA256.HashData(enc)), enc.Length, Convert.ToBase64String(enc), false);
        return SendAsync<KsefDto.SendInvoiceResponse>("wysyłka faktury", () => Req(HttpMethod.Post, $"sessions/online/{s.ReferenceNumber}/invoices", s.AccessToken, body), ct);
    }

    public Task<KsefDto.InvoiceStatus> GetInvoiceStatusAsync(OnlineSession s, string invoiceReference, CancellationToken ct = default) =>
        SendAsync<KsefDto.InvoiceStatus>("status faktury", () => Req(HttpMethod.Get, $"sessions/{s.ReferenceNumber}/invoices/{invoiceReference}", s.AccessToken), ct);

    /// <summary>Oczekiwanie na przetworzenie faktury (kod 200 — sukces z numerem KSeF; ≥ 300 — odrzucenie).</summary>
    public async Task<KsefDto.InvoiceStatus> WaitForInvoiceAsync(OnlineSession s, string invoiceReference, int maxPolls = 60, CancellationToken ct = default)
    {
        for (var i = 0; i < maxPolls; i++)
        {
            var st = await GetInvoiceStatusAsync(s, invoiceReference, ct);
            if (st.Status.Code >= 200) return st;
            await Task.Delay(_pollDelay, ct);
        }
        throw new KsefException("Przekroczono czas oczekiwania na przetworzenie faktury przez KSeF — sprawdź status później (numer referencyjny zapisano).");
    }

    public async Task CloseSessionAsync(OnlineSession s, CancellationToken ct = default) =>
        await SendAsync<string>("zamknięcie sesji", () => Req(HttpMethod.Post, $"sessions/online/{s.ReferenceNumber}/close", s.AccessToken), ct);

    public Task<string> GetUpoAsync(OnlineSession s, string invoiceReference, CancellationToken ct = default) =>
        SendAsync<string>("pobranie UPO", () => Req(HttpMethod.Get, $"sessions/{s.ReferenceNumber}/invoices/{invoiceReference}/upo", s.AccessToken), ct);

    /// <summary>Metadane faktur zakupowych (podmiot 2 = nabywca) w zakresie dat przyjęcia do KSeF.</summary>
    public async Task<List<KsefDto.InvoiceMetadata>> QueryPurchaseInvoicesAsync(string accessToken, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var all = new List<KsefDto.InvoiceMetadata>();
        for (var page = 0; page < 100; page++)
        {
            var resp = await SendAsync<KsefDto.QueryResponse>("wyszukanie faktur zakupowych", () => Req(HttpMethod.Post, $"invoices/query/metadata?pageOffset={page}&pageSize=100&sortOrder=Asc", accessToken,
                new KsefDto.QueryFilters("Subject2", new KsefDto.DateRange("PermanentStorage", from, to))), ct);
            all.AddRange(resp.Invoices);
            if (!resp.HasMore) break;
        }
        return all;
    }

    public Task<string> DownloadInvoiceAsync(string accessToken, string ksefNumber, CancellationToken ct = default) =>
        SendAsync<string>("pobranie faktury", () =>
        {
            var r = Req(HttpMethod.Get, $"invoices/ksef/{Uri.EscapeDataString(ksefNumber)}", accessToken);
            r.Headers.Accept.Clear();
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            return r;
        }, ct);

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
