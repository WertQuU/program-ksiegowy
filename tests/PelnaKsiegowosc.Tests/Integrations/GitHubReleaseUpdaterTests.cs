using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Updates;

namespace PelnaKsiegowosc.Tests.Integrations;

/// <summary>Nie korzysta z sieci — GitHub API i pliki wydania są emulowane.</summary>
public sealed class GitHubReleaseUpdaterTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = reply(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static string AssetName => OperatingSystem.IsWindows() ? "PelnaKsiegowosc-1.0.1-win-x64.msi" :
        "pelnaksiegowosc_1.0.1_amd64.deb";

    private static (HttpClient Client, byte[] Payload, string Hash) ReleaseClient(bool brokenDownload = false,
        bool brokenGitHubDigest = false, bool noChecksum = false)
    {
        var content = Encoding.UTF8.GetBytes("testowa bezpieczna zawartość instalatora");
        var actual = Convert.ToHexString(SHA256.HashData(content));
        var checksum = actual + "  " + AssetName + "\n";
        var sha = Encoding.UTF8.GetBytes(checksum);
        var assets = new List<object>
        {
            new {
                name = AssetName, size = content.Length,
                browser_download_url = "https://github.com/WertQuU/program-ksiegowy/releases/download/v1.0.1/" + AssetName,
                digest = "sha256:" + (brokenGitHubDigest ? new string('0', 64) : actual)
            }
        };
        if (!noChecksum) assets.Add(new
        {
            name = "SHA256SUMS.txt", size = sha.Length,
            browser_download_url = "https://github.com/WertQuU/program-ksiegowy/releases/download/v1.0.1/SHA256SUMS.txt",
            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(sha))
        });
        var release = JsonSerializer.Serialize(new
        {
            tag_name = "v1.0.1", draft = false, prerelease = false, assets
        });
        var client = new HttpClient(new Stub(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release) };
            if (path.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sha) };
            if (path.EndsWith(AssetName, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(brokenDownload
                        ? content.Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray()
                        : content)
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        return (client, content, actual);
    }

    [Fact]
    public async Task No_published_release_is_not_a_failure_or_an_update()
    {
        using var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new GitHubReleaseUpdater(http).CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Stable_release_with_matching_checksums_is_downloaded_verifiably()
    {
        var (http, payload, _) = ReleaseClient();
        using (http)
        {
            var updater = new GitHubReleaseUpdater(http);
            var available = await updater.CheckAsync(new Version(1, 0, 0));
            Assert.NotNull(available);
            Assert.Equal(new Version(1, 0, 1), available.Version);
            var root = Path.Combine(Path.GetTempPath(), "pk-updates-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var file = await updater.DownloadVerifiedAsync(available, root);
                Assert.Equal(payload, await File.ReadAllBytesAsync(file));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task Digest_disagreement_prevents_offering_installer()
    {
        var (http, _, _) = ReleaseClient(brokenGitHubDigest: true);
        using (http)
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                new GitHubReleaseUpdater(http).CheckAsync(new Version(1, 0, 0)));
            Assert.Contains("niespójne", error.Message);
        }
    }

    [Fact]
    public async Task Missing_manifest_prevents_installer_download()
    {
        var (http, _, _) = ReleaseClient(noChecksum: true);
        using (http)
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                new GitHubReleaseUpdater(http).CheckAsync(new Version(1, 0, 0)));
            Assert.Contains("SHA256SUMS.txt", error.Message);
        }
    }

    [Fact]
    public async Task Changed_installer_content_never_becomes_a_ready_update()
    {
        var (http, _, _) = ReleaseClient(brokenDownload: true);
        using (http)
        {
            var updater = new GitHubReleaseUpdater(http);
            var update = await updater.CheckAsync(new Version(1, 0, 0));
            Assert.NotNull(update);
            var root = Path.Combine(Path.GetTempPath(), "pk-updates-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                await Assert.ThrowsAsync<DomainException>(() => updater.DownloadVerifiedAsync(update, root));
                Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    [Theory]
    [InlineData("0A", "file.msi")]
    [InlineData("", "file.msi")]
    public void Invalid_checksums_are_rejected(string hex, string name)
    {
        Assert.Throws<DomainException>(() => GitHubReleaseUpdater.ParseChecksum(hex + "  " + name, name));
    }

    [Fact]
    public void Duplicate_hash_lines_are_rejected()
    {
        var sha = new string('A', 64);
        Assert.Throws<DomainException>(() =>
            GitHubReleaseUpdater.ParseChecksum(sha + "  file.msi\n" + sha + "  file.msi", "file.msi"));
    }
}
