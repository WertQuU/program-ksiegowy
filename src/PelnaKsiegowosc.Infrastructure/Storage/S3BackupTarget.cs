using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Configuration;

namespace PelnaKsiegowosc.Infrastructure.Storage;

public sealed record StorageTestResult(bool Connected, bool WriteOk, bool ReadOk, bool DeleteOk, string Message);

/// <summary>
/// Cel kopii w OVHcloud Object Storage (API zgodne z S3). Archiwa są szyfrowane po stronie klienta przed wysłaniem
/// (<see cref="ArchiveCrypto"/>); opcjonalnie dodatkowo SSE po stronie serwera. Object Storage nie zastępuje bazy danych —
/// przechowuje wyłącznie zaszyfrowane archiwa i kopie dokumentów.
/// Endpoint nie jest zakodowany na sztywno — użytkownik wybiera endpoint regionu zgodnie z dokumentacją OVHcloud.
/// </summary>
public sealed class S3BackupTarget : IBackupTarget, IDisposable
{
    /// <summary>Podpowiedzi endpointów OVHcloud (S3 „Standard”). WYMAGAJĄ weryfikacji w aktualnej dokumentacji OVHcloud dla wybranego regionu.</summary>
    public static readonly IReadOnlyList<(string Region, string Endpoint)> OvhEndpointHints = new[]
    {
        ("gra", "https://s3.gra.io.cloud.ovh.net"), ("sbg", "https://s3.sbg.io.cloud.ovh.net"), ("rbx", "https://s3.rbx.io.cloud.ovh.net"),
        ("de", "https://s3.de.io.cloud.ovh.net"), ("waw", "https://s3.waw.io.cloud.ovh.net"), ("uk", "https://s3.uk.io.cloud.ovh.net"), ("bhs", "https://s3.bhs.io.cloud.ovh.net")
    };

    private readonly ObjectStorageSettings _settings;
    private readonly AmazonS3Client _client;

    public S3BackupTarget(ObjectStorageSettings settings, string accessKey, string secretKey)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new DomainException("Podaj prawidłowy adres endpointu Object Storage (https://...).");
        if (string.IsNullOrWhiteSpace(settings.Bucket)) throw new DomainException("Podaj nazwę bucketu.");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)) throw new DomainException("Brak klucza dostępowego Object Storage.");
        _settings = settings;
        var config = new AmazonS3Config
        {
            ServiceURL = settings.Endpoint,
            ForcePathStyle = settings.ForcePathStyle,
            AuthenticationRegion = string.IsNullOrWhiteSpace(settings.Region) ? null : settings.Region,
            Timeout = TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)),
            MaxErrorRetry = 4,
            // dostawcy zgodni z S3 nie zawsze obsługują domyślne sumy CRC nowych wersji SDK
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    }

    public static S3BackupTarget FromSecrets(ObjectStorageSettings s, ISecretStore secrets) =>
        new(s, secrets.Get(s.AccessKeySecretName) ?? throw new DomainException("Brak zapisanego klucza dostępowego Object Storage (Access Key)."),
            secrets.Get(s.SecretKeySecretName) ?? throw new DomainException("Brak zapisanego klucza tajnego Object Storage (Secret Key)."));

    public string Name => $"Object Storage {_settings.Endpoint}/{_settings.Bucket}";

    private string Key(string objectName) => (_settings.Prefix ?? "").TrimStart('/') + objectName;

    public async Task<string> UploadAsync(string localFile, string objectName, string sha256, CancellationToken ct = default)
    {
        var key = Key(objectName);
        var req = new TransferUtilityUploadRequest
        {
            BucketName = _settings.Bucket,
            Key = key,
            FilePath = localFile,
            ContentType = "application/octet-stream",
            PartSize = 16 * 1024 * 1024
        };
        req.Metadata.Add("sha256", sha256);
        req.Metadata.Add("app", "pelna-ksiegowosc");
        if (_settings.ServerSideEncryption) req.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        using var tu = new TransferUtility(_client);
        try
        {
            await tu.UploadAsync(req, ct);
        }
        catch
        {
            // przerwane wysyłanie wieloczęściowe — usunięcie niedokończonych części, aby nie generowały kosztów
            await AbortIncompleteUploadsAsync(TimeSpan.Zero, key, CancellationToken.None);
            throw;
        }
        var head = await _client.GetObjectMetadataAsync(_settings.Bucket, key, ct);
        var expectedSize = new FileInfo(localFile).Length;
        if (head.ContentLength != expectedSize) throw new IOException($"Weryfikacja po wysłaniu: rozmiar obiektu {head.ContentLength} ≠ {expectedSize}.");
        if (head.Metadata["sha256"] is { } meta && !string.Equals(meta, sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Weryfikacja po wysłaniu: niezgodna suma SHA-256 w metadanych obiektu.");
        return key;
    }

    public async Task<IReadOnlyList<(string Key, long Size, DateTime LastModifiedUtc)>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<(string, long, DateTime)>();
        string? token = null;
        do
        {
            var resp = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _settings.Bucket, Prefix = _settings.Prefix, ContinuationToken = token }, ct);
            foreach (var o in resp.S3Objects ?? new List<S3Object>())
                result.Add((o.Key, o.Size ?? 0, (o.LastModified ?? DateTime.UtcNow).ToUniversalTime()));
            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (token is not null);
        return result.OrderByDescending(r => r.Item3).ToList();
    }

    /// <summary>Pobranie archiwum z weryfikacją sumy SHA-256 zapisanej w metadanych.</summary>
    public async Task DownloadAsync(string key, string localFile, CancellationToken ct = default)
    {
        using var resp = await _client.GetObjectAsync(_settings.Bucket, key, ct);
        var tmp = localFile + ".partial";
        await using (var fs = File.Create(tmp))
            await resp.ResponseStream.CopyToAsync(fs, ct);
        var sha = await DataDump.Sha256FileAsync(tmp, ct);
        var expected = resp.Metadata["sha256"];
        if (!string.IsNullOrEmpty(expected) && !string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new IOException("Pobrane archiwum ma niezgodną sumę SHA-256 — plik uszkodzony w transmisji lub w magazynie.");
        }
        File.Move(tmp, localFile, true);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) => _client.DeleteObjectAsync(_settings.Bucket, key, ct);

    /// <summary>Usunięcie najstarszych archiwów ponad limit zasad przechowywania.</summary>
    public async Task<int> ApplyRetentionAsync(int keep, CancellationToken ct = default)
    {
        if (keep <= 0) return 0;
        var items = (await ListAsync(ct)).Where(i => i.Key.EndsWith(BackupService.Extension)).ToList();
        var n = 0;
        foreach (var old in items.Skip(keep)) { await DeleteAsync(old.Key, ct); n++; }
        return n;
    }

    public async Task<int> AbortIncompleteUploadsAsync(TimeSpan olderThan, string? keyPrefix = null, CancellationToken ct = default)
    {
        var resp = await _client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = _settings.Bucket, Prefix = keyPrefix ?? _settings.Prefix }, ct);
        var n = 0;
        foreach (var u in resp.MultipartUploads ?? new List<MultipartUpload>())
        {
            if ((u.Initiated ?? DateTime.UtcNow) > DateTime.UtcNow - olderThan) continue;
            await _client.AbortMultipartUploadAsync(_settings.Bucket, u.Key, u.UploadId, ct);
            n++;
        }
        return n;
    }

    /// <summary>Test połączenia: zapis, odczyt i usunięcie obiektu testowego. Komunikaty bez ujawniania kluczy.</summary>
    public async Task<StorageTestResult> TestAsync(CancellationToken ct = default)
    {
        var key = Key($"test-polaczenia-{Guid.NewGuid():N}.txt");
        var content = $"Pełna Księgowość P.S.A. — test {DateTime.UtcNow:O}";
        bool connected = false, write = false, read = false, delete = false;
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _settings.Bucket, Prefix = _settings.Prefix, MaxKeys = 1 }, ct);
            connected = true;
            var put = new PutObjectRequest { BucketName = _settings.Bucket, Key = key, ContentBody = content };
            if (_settings.ServerSideEncryption) put.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            await _client.PutObjectAsync(put, ct);
            write = true;
            using (var g = await _client.GetObjectAsync(_settings.Bucket, key, ct))
            using (var sr = new StreamReader(g.ResponseStream))
                read = await sr.ReadToEndAsync(ct) == content;
            await _client.DeleteObjectAsync(_settings.Bucket, key, ct);
            delete = true;
            return new StorageTestResult(true, true, read, true, read ? "Połączenie, zapis, odczyt i usuwanie działają poprawnie." : "Odczytana treść różni się od zapisanej.");
        }
        catch (AmazonS3Exception ex)
        {
            var msg = ex.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Odmowa dostępu (403) — sprawdź uprawnienia użytkownika S3 do bucketu.",
                HttpStatusCode.NotFound => "Bucket nie istnieje (404) lub błędny region/endpoint.",
                _ => $"Błąd usługi ({(int)ex.StatusCode}): {ex.ErrorCode}"
            };
            return new StorageTestResult(connected, write, read, delete, msg);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AmazonClientException)
        {
            return new StorageTestResult(connected, write, read, delete, "Brak połączenia z endpointem: " + ex.Message);
        }
    }

    public void Dispose() => _client.Dispose();
}
