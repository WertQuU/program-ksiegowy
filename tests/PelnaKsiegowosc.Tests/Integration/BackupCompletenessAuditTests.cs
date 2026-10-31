using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

/// <summary>Regresje integralności kopii pełnej: załącznik brakujący lub zmieniony nie może dawać „zweryfikowanej” kopii.</summary>
public class BackupCompletenessAuditTests
{
    private static readonly BackupKey Key = new BackupKey.Passphrase("Testowe-Haslo-Pelnej-Kopii-2026");

    [Fact]
    public async Task Full_backup_rejects_missing_document_attachment()
    {
        var (books, store) = await BackupTests.BooksWithAttachmentAsync();
        await using var _ = books;
        var attachment = await books.Db.Attachments.SingleAsync();
        var path = Path.Combine(store.RootPath, attachment.StoragePath.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(path);

        var destination = Path.Combine(books.Directory, "kopie");
        var service = new BackupService(books.Clock, Path.Combine(books.Directory, "tmp"));
        var profile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = books.DbPath };
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateAsync(books.Db, profile, store, null, new BackupOptions(Key, destination)));

        Assert.Contains("Brak pliku załącznika", ex.Message);
        Assert.Empty(Directory.GetFiles(destination, "*" + BackupService.Extension));
        books.Db.ChangeTracker.Clear();
        var record = await books.Db.BackupRecords.SingleAsync();
        Assert.Equal("BŁĄD", record.Status);
        Assert.False(record.Verified);
    }

    [Fact]
    public async Task Full_backup_rejects_changed_attachment_content()
    {
        var (books, store) = await BackupTests.BooksWithAttachmentAsync();
        await using var _ = books;
        var attachment = await books.Db.Attachments.SingleAsync();
        var path = Path.Combine(store.RootPath, attachment.StoragePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(path, "Inna zawartość niż w metadanych załącznika");

        var destination = Path.Combine(books.Directory, "kopie");
        var service = new BackupService(books.Clock, Path.Combine(books.Directory, "tmp"));
        var profile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = books.DbPath };
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateAsync(books.Db, profile, store, null, new BackupOptions(Key, destination)));

        Assert.Contains("SHA-256 niezgodna", ex.Message);
        Assert.Empty(Directory.GetFiles(destination, "*" + BackupService.Extension));
        books.Db.ChangeTracker.Clear();
        Assert.Equal("BŁĄD", (await books.Db.BackupRecords.SingleAsync()).Status);
    }
    [Fact]
    public async Task Server_restore_refuses_nonempty_attachment_store_without_overwriting_existing_files()
    {
        var (source, files) = await BackupTests.BooksWithAttachmentAsync();
        await using var _ = source;
        var service = new BackupService(source.Clock, Path.Combine(source.Directory, "staging"));
        var sourceProfile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = source.DbPath };
        var backup = await service.CreateAsync(source.Db, sourceProfile, files, null,
            new BackupOptions(Key, Path.Combine(source.Directory, "copies")));

        await using var target = TestBooks.Open(Path.Combine(source.Directory, "new-empty.db"));
        await target.Database.MigrateAsync();
        var root = Path.Combine(source.Directory, "target-attachments");
        var targetFiles = new LocalFileStore(root, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var protectedFile = Path.Combine(root, "existing-evidence.bin");
        await File.WriteAllTextAsync(protectedFile, "wazny istniejący dokument");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.RestoreToDatabaseAsync(backup.FilePath, Key, target, targetFiles));
        Assert.Contains("pustego magazynu", ex.Message);
        Assert.Equal("wazny istniejący dokument", await File.ReadAllTextAsync(protectedFile));
        Assert.Equal(0, await target.Companies.CountAsync());
    }

    [Fact]
    public async Task Sqlite_restore_does_not_replace_unrelated_files_in_destination()
    {
        var (source, files) = await BackupTests.BooksWithAttachmentAsync();
        await using var _ = source;
        var service = new BackupService(source.Clock, Path.Combine(source.Directory, "restore-work"));
        var result = await service.CreateAsync(source.Db,
            new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = source.DbPath },
            files, null, new BackupOptions(Key, Path.Combine(source.Directory, "archives")));

        var target = Path.Combine(source.Directory, "occupied-target");
        Directory.CreateDirectory(target);
        var evidence = Path.Combine(target, "existing.txt");
        await File.WriteAllTextAsync(evidence, "zachowany dokument");
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.RestoreToSqliteAsync(result.FilePath, Key, target,
                SqliteEncryption.NewKey(), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Assert.Contains("pustego", error.Message);
        Assert.Equal("zachowany dokument", await File.ReadAllTextAsync(evidence));
        Assert.False(File.Exists(Path.Combine(target, "ksiegi.db")));
        Assert.Empty(Directory.EnumerateDirectories(source.Directory, ".occupied-target-odtwarzanie-*"));
    }

    [Fact]
    public async Task Server_restore_does_not_commit_books_when_attachment_store_reports_wrong_content()
    {
        var (source, attachments) = await BackupTests.BooksWithAttachmentAsync();
        await using var _ = source;
        var service = new BackupService(source.Clock, Path.Combine(source.Directory, "restore-stage"));
        var backup = await service.CreateAsync(source.Db,
            new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = source.DbPath },
            attachments, null, new BackupOptions(Key, Path.Combine(source.Directory, "backups")));

        await using var target = TestBooks.Open(Path.Combine(source.Directory, "empty-restore.db"));
        await target.Database.MigrateAsync();
        var dest = new CorruptingFileStore(new LocalFileStore(
            Path.Combine(source.Directory, "fresh-attachments"),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreToDatabaseAsync(backup.FilePath, Key, target, dest));
        Assert.Equal(0, await target.Companies.CountAsync());
    }

    private sealed class CorruptingFileStore(LocalFileStore inner) : IFileStore
    {
        public string RootPath => inner.RootPath;
        public Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default) =>
            inner.ExistsAsync(storagePath, ct);
        public Task<Stream> OpenAsync(string storagePath, CancellationToken ct = default) =>
            inner.OpenAsync(storagePath, ct);
        public Task<(string storagePath, string sha256, long size)> SaveAsync(Stream content, string suggestedName, CancellationToken ct = default) =>
            inner.SaveAsync(content, suggestedName, ct);
        public async Task<(string sha256, long size)> SaveAtAsync(string storagePath, Stream content, CancellationToken ct = default)
        {
            var (_, size) = await inner.SaveAtAsync(storagePath, content, ct);
            return (new string('0', 64), size);
        }
    }

}
