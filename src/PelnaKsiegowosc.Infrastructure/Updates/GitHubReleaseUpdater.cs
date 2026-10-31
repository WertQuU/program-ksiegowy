using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Infrastructure.Updates;

/// <summary>
/// Aktualizator bez tokenu dla publicznych wydań z repozytorium producenta.
/// Nie uruchamia kodu z gałęzi main. Wymaga wydania stable i SHA256SUMS.txt.
/// Instalację wykonuje system operacyjny po zgodzie użytkownika.
/// </summary>
public sealed class GitHubReleaseUpdater
{
    public const string ReleasesUrl = "https://github.com/WertQuU/program-ksiegowy/releases";
    private const string ApiUrl = "https://api.github.com/repos/WertQuU/program-ksiegowy/releases/latest";
    private const long MaxInstallerSize = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _http;

    public GitHubReleaseUpdater(HttpClient? http = null) => _http = http ?? SharedClient;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PelnaKsiegowosc", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public sealed record UpdateInfo(Version Version, string Tag, string AssetName, long Size,
        Uri DownloadUrl, string Sha256, Uri ReleasePage);

    private sealed record Asset(string Name, long Size, Uri Uri, string? GitHubDigest);

    /// <summary>Sprawdza stabilne wydania. Brak wydań lub nowszej wersji daje null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 ||
            !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            throw new DomainException("Automatyczne pobieranie instalatora jest dostępne wyłącznie dla Windows x64 i Linux x64.");

        using var response = await _http.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 24 }, ct);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new DomainException("Wydanie GitHub ma nieprawidłowy numer wersji.");
        if (version <= currentVersion) return null;

        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => new Asset(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("size").GetInt64(),
                new Uri(a.GetProperty("browser_download_url").GetString() ?? "", UriKind.Absolute),
                a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null))
            .ToList();
        var ext = OperatingSystem.IsWindows() ? ".msi" : ".deb";
        var candidates = assets.Where(a => a.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) &&
            (OperatingSystem.IsWindows() || a.Name.EndsWith("_amd64.deb", StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count != 1) throw new DomainException("Wydanie nie zawiera jednoznacznego instalatora " + ext + " dla tego systemu.");
        var asset = candidates[0];
        if (asset.Size <= 0 || asset.Size > MaxInstallerSize || !SafeName(asset.Name))
            throw new DomainException("Instalator wydania ma niedozwoloną nazwę lub wielkość.");
        var checksum = assets.SingleOrDefault(a => a.Name == "SHA256SUMS.txt")
            ?? throw new DomainException("Wydanie nie zawiera SHA256SUMS.txt — aktualizacja zablokowana.");
        if (checksum.Size <= 0 || checksum.Size > 128 * 1024)
            throw new DomainException("Lista sum kontrolnych aktualizacji ma nieprawidłowy rozmiar.");

        VerifyReleaseUrl(asset.Uri, tag);
        VerifyReleaseUrl(checksum.Uri, tag);
        using var checksumsResponse = await _http.GetAsync(checksum.Uri, HttpCompletionOption.ResponseHeadersRead, ct);
        checksumsResponse.EnsureSuccessStatusCode();
        VerifyDestination(checksumsResponse.RequestMessage?.RequestUri);
        if (checksumsResponse.Content.Headers.ContentLength is > 131072)
            throw new DomainException("Lista sum kontrolnych jest zbyt duża.");
        var checksums = await checksumsResponse.Content.ReadAsByteArrayAsync(ct);
        if (checksums.Length > 131072) throw new DomainException("Lista sum kontrolnych jest zbyt duża.");
        var hash = ParseChecksum(System.Text.Encoding.UTF8.GetString(checksums), asset.Name);
        if (asset.GitHubDigest is { Length: > 0 } digest &&
            !string.Equals(digest, "sha256:" + hash, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("SHA-256 instalatora w GitHub API i SHA256SUMS.txt są niespójne.");

        var page = new Uri(ReleasesUrl + "/tag/" + Uri.EscapeDataString(tag));
        return new UpdateInfo(version, tag, asset.Name, asset.Size, asset.Uri, hash, page);
    }

    /// <summary>Pobiera i sprawdza SHA-256 PRZED opublikowaniem pliku instalatora na dysku.</summary>
    public async Task<string> DownloadVerifiedAsync(UpdateInfo update, string targetRoot, CancellationToken ct = default)
    {
        VerifyReleaseUrl(update.DownloadUrl, update.Tag);
        if (!SafeName(update.AssetName) || update.Size <= 0 || update.Size > MaxInstallerSize ||
            !ValidSha256(update.Sha256))
            throw new DomainException("Metadane instalatora nie są poprawne.");
        var targetDir = Path.Combine(Path.GetFullPath(targetRoot), "aktualizacja-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, update.AssetName);
        var partial = path + ".part";
        try
        {
            using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            VerifyDestination(response.RequestMessage?.RequestUri);
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw new DomainException("Instalator ma rozmiar inny niż zadeklarowany w wydaniu.");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long downloaded = 0;
            try
            {
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    downloaded += count;
                    if (downloaded > update.Size || downloaded > MaxInstallerSize)
                        throw new DomainException("Pobrany instalator przekracza rozmiar podany w wydaniu.");
                    hasher.AppendData(buffer, 0, count);
                    await dest.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            if (downloaded != update.Size)
                throw new DomainException("Instalator pobrano tylko częściowo.");
            var actual = hasher.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(update.Sha256)))
                throw new DomainException("SHA-256 instalatora jest niezgodne z oficjalnym wydaniem — instalacja zablokowana.");
            await dest.FlushAsync(ct);
            dest.Close();
            File.Move(partial, path);
            return path;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (Directory.Exists(targetDir) && !Directory.EnumerateFileSystemEntries(targetDir).Any())
                Directory.Delete(targetDir);
            throw;
        }
    }

    public static string ParseChecksum(string manifest, string assetName)
    {
        if (!SafeName(assetName)) throw new DomainException("Niedozwolona nazwa instalatora.");
        var found = new List<string>();
        foreach (var line in manifest.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^([A-Fa-f0-9]{64}) [ *](.+)$", RegexOptions.CultureInvariant);
            if (match.Success && match.Groups[2].Value == assetName)
                found.Add(match.Groups[1].Value.ToUpperInvariant());
        }
        if (found.Count != 1)
            throw new DomainException("Brak jednoznacznej sumy SHA-256 dla instalatora " + assetName + ".");
        return found[0];
    }

    private static bool SafeName(string name) =>
        Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,180}$", RegexOptions.CultureInvariant);
    private static bool ValidSha256(string hex) => Regex.IsMatch(hex, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant);

    private static void VerifyReleaseUrl(Uri uri, string tag)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/WertQuU/program-ksiegowy/releases/download/" + Uri.EscapeDataString(tag) + "/",
                StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0)
            throw new DomainException("Nieprawidłowy adres pliku GitHub Releases.");
    }

    private static void VerifyDestination(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            !new[] { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" }
                .Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("GitHub przekierował pobieranie do niezaufanego serwera.");
    }
}
