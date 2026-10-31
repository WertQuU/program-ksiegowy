using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Application.Documents;
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

}
