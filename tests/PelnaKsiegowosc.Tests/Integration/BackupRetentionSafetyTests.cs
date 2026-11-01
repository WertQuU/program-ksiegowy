using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class BackupRetentionSafetyTests
{
    [Fact]
    public async Task Failed_remote_upload_keeps_previous_verified_local_backup_despite_retention_limit()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new BackupService(books.Clock, Path.Combine(books.Directory, "tmp"));
        var target = Path.Combine(books.Directory, "kopie");
        var store = new LocalFileStore(Path.Combine(books.Directory, "Zalaczniki"));
        var profile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = books.DbPath };
        var key = new BackupKey.Passphrase("Testowe-Haslo-Kopii-Retencja-2026");
        var options = new BackupOptions(key, target, RetainCount: 1);

        var previous = await service.CreateAsync(books.Db, profile, store, null, options);
        Assert.True(previous.Verified);
        Assert.True(File.Exists(previous.FilePath));

        var failedRemote = await service.CreateAsync(books.Db, profile, store, null, options, new UnavailableRemote());
        Assert.True(failedRemote.Verified);
        Assert.NotNull(failedRemote.RemoteError);
        Assert.Null(failedRemote.RemoteKey);
        Assert.True(File.Exists(previous.FilePath));
        Assert.True(File.Exists(failedRemote.FilePath));
        Assert.Equal(2, Directory.GetFiles(target, "*.pkbak").Length);
        Assert.True((await service.VerifyAsync(previous.FilePath, key)).Ok);
        Assert.True((await service.VerifyAsync(failedRemote.FilePath, key)).Ok);

        // Retencja nadal działa, gdy kolejna kopia kończy się bez błędu wysyłki.
        var localOnly = await service.CreateAsync(books.Db, profile, store, null, options);
        Assert.True(localOnly.Verified);
        Assert.True(File.Exists(localOnly.FilePath));
        Assert.Single(Directory.GetFiles(target, "*.pkbak"));
    }

    private sealed class UnavailableRemote : IBackupTarget
    {
        public string Name => "testowy niedostępny magazyn";
        public Task<string> UploadAsync(string localFile, string objectName, string sha256,
            CancellationToken ct = default) => throw new HttpRequestException("Symulowany błąd S3");
        public Task<IReadOnlyList<(string Key, long Size, DateTime LastModifiedUtc)>> ListAsync(
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadAsync(string key, string localFile, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
