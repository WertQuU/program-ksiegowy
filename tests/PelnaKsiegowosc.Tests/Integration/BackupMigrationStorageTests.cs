using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Infrastructure.Migration;
using PelnaKsiegowosc.Infrastructure.Storage;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public class BackupTests
{
    private static readonly BackupKey Pass = new BackupKey.Passphrase("Bardzo-Tajne-Haslo-Kopii-2026");

    private static async Task<(TestBooks t, LocalFileStore store)> BooksWithAttachmentAsync()
    {
        var t = await FullYearScenarioTests.BuildFullYearAsync(closeYear: false);
        var store = new LocalFileStore(Path.Combine(t.Directory, "Zalaczniki"));
        var doc = await t.Db.Documents.FirstAsync(d => d.Kind == DocumentKind.FakturaSprzedazy);
        await new DocumentService(t.Db, t.User, t.Clock).AddAttachmentAsync(doc.Id, store, "faktura.pdf", new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 testowy załącznik")));
        return (t, store);
    }

    private static DatabaseProfile Profile(TestBooks t) => new() { Engine = DatabaseEngine.Sqlite, SqlitePath = t.DbPath };

    [Fact]
    public async Task Backup_is_encrypted_verified_and_restores_to_clean_directory_with_identical_books()
    {
        var (t, store) = await BooksWithAttachmentAsync();
        await using var _ = t;
        var svc = new BackupService(t.Clock, Path.Combine(t.Directory, "tmp"));
        var target = Path.Combine(t.Directory, "kopie");
        var res = await svc.CreateAsync(t.Db, Profile(t), store, "{\"konfiguracja\":true}", new BackupOptions(Pass, target));
        Assert.True(res.Verified);
        Assert.EndsWith(".pkbak", res.FilePath);
        Assert.Empty(res.Manifest.Warnings);
        Assert.Contains(res.Manifest.Files, f => f.Path == "baza/ksiegi.db");
        Assert.Contains(res.Manifest.Files, f => f.Path.StartsWith("zalaczniki/"));
        Assert.Contains(res.Manifest.Tables, x => x.Table == "JournalEntries" && x.Rows > 0);
        Assert.Equal(res.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(res.FilePath))));
        Assert.DoesNotContain(Directory.GetFiles(target), f => f.EndsWith(".partial"));
        // archiwum nie zawiera jawnych danych
        var raw = File.ReadAllBytes(res.FilePath);
        Assert.DoesNotContain("Testowa Prosta", Encoding.UTF8.GetString(raw));
        Assert.Equal(1, await t.Db.BackupRecords.CountAsync(b => b.Verified && b.Status == "OK"));

        var restoreDir = Path.Combine(t.Directory, "odtworzona");
        var restored = await svc.RestoreToSqliteAsync(res.FilePath, Pass, restoreDir);
        Assert.True(restored.Ok, string.Join("; ", restored.Problems));
        await using var db2 = TestBooks.Open(restored.DatabasePath);
        var tbOrig = await new LedgerQueries(t.Db).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        var tbRest = await new LedgerQueries(db2).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        Assert.Equal(tbOrig.Select(x => (x.Account.Number, x.Balance)), tbRest.Select(x => (x.Account.Number, x.Balance)));
        var integrity = await new IntegrityService(db2, t.Clock).FullCheckAsync(t.Company.Id);
        Assert.True(integrity.AllOk, string.Join("; ", integrity.Results.Where(r => !r.Ok).Select(r => r.Details)));
        var att = await db2.Attachments.SingleAsync();
        var bytes = await new DocumentService(db2, t.User, t.Clock).ReadAttachmentAsync(att.Id, new LocalFileStore(restored.AttachmentsPath));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes));

        // ponowne odtworzenie do tego samego katalogu — odmowa (brak nadpisania)
        await Assert.ThrowsAsync<DomainException>(() => svc.RestoreToSqliteAsync(res.FilePath, Pass, restoreDir));
    }

    [Fact]
    public async Task Wrong_key_tampering_and_truncation_are_detected()
    {
        var (t, store) = await BooksWithAttachmentAsync();
        await using var _ = t;
        var svc = new BackupService(t.Clock, Path.Combine(t.Directory, "tmp"));
        var res = await svc.CreateAsync(t.Db, Profile(t), store, null, new BackupOptions(Pass, Path.Combine(t.Directory, "kopie")));

        var wrong = await svc.VerifyAsync(res.FilePath, new BackupKey.Passphrase("Inne-Haslo-Kopii-2026!"));
        Assert.False(wrong.Ok);
        Assert.Contains(wrong.Problems, p => p.Contains("hasło"));

        var tampered = res.FilePath + ".zmieniony";
        var data = File.ReadAllBytes(res.FilePath);
        data[data.Length / 2] ^= 0x01;
        File.WriteAllBytes(tampered, data);
        Assert.False((await svc.VerifyAsync(tampered, Pass)).Ok);

        var truncated = res.FilePath + ".obciety";
        File.WriteAllBytes(truncated, File.ReadAllBytes(res.FilePath)[..^100]);
        Assert.False((await svc.VerifyAsync(truncated, Pass)).Ok);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.RestoreToSqliteAsync(tampered, Pass, Path.Combine(t.Directory, "x")));
        Assert.False(File.Exists(Path.Combine(t.Directory, "x", "ksiegi.db")));
    }

    [Fact]
    public async Task Raw_key_with_recovery_code_round_trip()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var recovery = ArchiveCrypto.FormatRecoveryKey(key);
        Assert.Equal(key, ArchiveCrypto.ParseRecoveryKey(recovery));
        var plain = RandomNumberGenerator.GetBytes(3 * ArchiveCrypto.ChunkSize + 123); // wiele fragmentów
        using var enc = new MemoryStream();
        await ArchiveCrypto.EncryptAsync(new MemoryStream(plain), enc, new BackupKey.RawKey(key), Guid.NewGuid(), DateTime.UtcNow);
        enc.Position = 0;
        using var dec = new MemoryStream();
        await ArchiveCrypto.DecryptAsync(enc, dec, new BackupKey.RawKey(ArchiveCrypto.ParseRecoveryKey(recovery)));
        Assert.Equal(plain, dec.ToArray());
        // usunięcie ostatniego fragmentu (obcięcie na granicy) jest wykrywane
        var full = enc.ToArray();
        var cut = full[..(full.Length - (123 + 21))];
        await Assert.ThrowsAsync<CryptographicException>(() => ArchiveCrypto.DecryptAsync(new MemoryStream(cut), new MemoryStream(), new BackupKey.RawKey(key)));
        await Assert.ThrowsAsync<DomainException>(() => ArchiveCrypto.EncryptAsync(new MemoryStream(plain), new MemoryStream(), new BackupKey.Passphrase("krotkie"), Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public async Task Failure_during_backup_leaves_no_partial_archive_and_is_reported()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new BackupService(t.Clock, Path.Combine(t.Directory, "tmp"));
        var store = new LocalFileStore(Path.Combine(t.Directory, "Zalaczniki"));
        var target = Path.Combine(t.Directory, "kopie");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CreateAsync(t.Db, Profile(t), store, null, new BackupOptions(Pass, target), null, cts.Token));
        Assert.Empty(Directory.GetFiles(target));
        t.Db.ChangeTracker.Clear();
        var rec = await t.Db.BackupRecords.SingleAsync();
        Assert.Equal("BŁĄD", rec.Status);
        Assert.False(rec.Verified);
        // cel zdalny niedostępny — kopia lokalna zachowana, błąd wysyłki raportowany
        var res = await svc.CreateAsync(t.Db, Profile(t), store, null, new BackupOptions(Pass, target), new FailingTarget());
        Assert.True(File.Exists(res.FilePath));
        Assert.Contains("nie powiodło się", res.RemoteError);
        Assert.Equal("OK LOKALNIE, BŁĄD WYSYŁKI", (await t.Db.BackupRecords.OrderByDescending(b => b.CreatedAtUtc).FirstAsync(b => b.Verified)).Status);
    }

    private sealed class FailingTarget : IBackupTarget
    {
        public string Name => "niedostępny serwer";
        public Task<string> UploadAsync(string localFile, string objectName, string sha256, CancellationToken ct = default) => throw new HttpRequestException("Brak połączenia");
        public Task<IReadOnlyList<(string Key, long Size, DateTime LastModifiedUtc)>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadAsync(string key, string localFile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

public class PostgresTests
{
    internal static async Task<(KsiegiDbContext db, string cs, string dbName)> CreateEmptyPostgresAsync()
    {
        var baseCs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PK_TEST_PG")!);
        var name = "pk_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(baseCs.ConnectionString) { Database = "postgres" }.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", c);
            await cmd.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(baseCs.ConnectionString) { Database = name }.ConnectionString;
        return (DbContextFactory.Create(DatabaseEngine.PostgreSql, cs), cs, name);
    }

    [PostgresFact]
    public async Task Sqlite_to_postgres_migration_preserves_books_ids_and_attachments()
    {
        var t = await FullYearScenarioTests.BuildFullYearAsync(closeYear: true);
        await using var _ = t;
        var store = new LocalFileStore(Path.Combine(t.Directory, "Zalaczniki"));
        var doc = await t.Db.Documents.FirstAsync();
        await new DocumentService(t.Db, t.User, t.Clock).AddAttachmentAsync(doc.Id, store, "skan.pdf", new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 skan")));
        var (pg, _, _) = await CreateEmptyPostgresAsync();
        await using var __ = pg;
        var targetStore = new LocalFileStore(Path.Combine(t.Directory, "Zalaczniki-PG"));
        var backup = new BackupService(t.Clock, Path.Combine(t.Directory, "tmp"));
        var migrator = new DatabaseMigrator(t.Clock, Path.Combine(t.Directory, "tmp"));
        var report = await migrator.MigrateAsync(t.Db, "SQLite test", store, pg, "PostgreSQL test", targetStore,
            async () => (await backup.CreateAsync(t.Db, new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = t.DbPath }, store, null,
                new BackupOptions(new BackupKey.Passphrase("Haslo-Przed-Migracja-1"), Path.Combine(t.Directory, "kopie")))).FilePath);
        Assert.True(report.Success, report.ToText());
        Assert.Equal(1, report.AttachmentsCopied);
        Assert.NotNull(report.PreMigrationBackup);

        var tbS = await new LedgerQueries(t.Db).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        var tbT = await new LedgerQueries(pg).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        Assert.Equal(tbS.Select(x => (x.Account.Id, x.Balance)), tbT.Select(x => (x.Account.Id, x.Balance)));
        var integrity = await new IntegrityService(pg, t.Clock).FullCheckAsync(t.Company.Id);
        Assert.True(integrity.AllOk, string.Join("; ", integrity.Results.Where(r => !r.Ok).Select(r => r.Area + ": " + r.Details)));
        // źródło nienaruszone
        Assert.True(await t.Db.JournalEntries.AnyAsync());

        // wyzwalacze niezmienności działają również w PostgreSQL
        var id = await pg.JournalEntries.Where(e => e.Status == Domain.Ledger.EntryStatus.Zaksiegowany).Select(e => e.Id).FirstAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => pg.Database.ExecuteSqlRawAsync("UPDATE \"JournalEntries\" SET \"Description\" = 'x' WHERE \"Id\" = {0}", id));
        // ponowna migracja do niepustej bazy jest odrzucana, dane bez zmian
        var again = await migrator.MigrateAsync(t.Db, "SQLite", store, pg, "PG", targetStore, () => Task.FromResult("pominięto"));
        Assert.False(again.Success);
    }

    [PostgresFact]
    public async Task Posting_works_directly_on_postgres_and_connection_loss_is_reported()
    {
        var (pg, cs, name) = await CreateEmptyPostgresAsync();
        await pg.DisposeAsync();
        var connector = new DatabaseConnector(new InMemorySecretStore());
        var b = new NpgsqlConnectionStringBuilder(cs);
        var profile = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Host = b.Host, Port = b.Port, Database = name, Username = b.Username, SslMode = PgSslMode.Disable };
        connector.SavePassword(profile, b.Password!);
        var ok = await connector.TestAsync(profile);
        Assert.True(ok.Success, ok.Message);
        var (db, err) = await connector.OpenActiveAsync(profile);
        Assert.Null(err);
        await using (db)
        {
            Assert.Empty(await db!.Database.GetPendingMigrationsAsync());
        }

        var down = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Host = "127.0.0.1", Port = 1, Database = name, Username = b.Username, SslMode = PgSslMode.Disable, TimeoutSeconds = 3 };
        var fail = await connector.TestAsync(down);
        Assert.False(fail.Success);
        Assert.DoesNotContain(b.Password!, fail.Message);
        var (db2, err2) = await connector.OpenActiveAsync(down);
        Assert.Null(db2);
        Assert.Contains("nie przełącza się automatycznie", err2);

        var wrongPwd = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Host = b.Host, Port = b.Port, Database = name, Username = b.Username, SslMode = PgSslMode.Disable };
        connector.SavePassword(wrongPwd, "zle-haslo");
        var auth = await connector.TestAsync(wrongPwd);
        Assert.False(auth.Success);
        Assert.Contains("Uwierzytelnienie", auth.Message);
    }
}

public class ObjectStorageTests
{
    private static ObjectStorageSettings Settings(string bucket) => new()
    {
        Enabled = true,
        Endpoint = Environment.GetEnvironmentVariable("PK_TEST_S3_ENDPOINT"),
        Region = "us-east-1",
        Bucket = bucket,
        Prefix = "testy/",
        ForcePathStyle = true
    };

    [S3Fact]
    public async Task Backup_upload_list_download_verify_and_connection_test()
    {
        var bucket = "pk-" + Guid.NewGuid().ToString("N")[..10];
        using (var admin = new Amazon.S3.AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials("test", "test"),
                   new Amazon.S3.AmazonS3Config { ServiceURL = Settings(bucket).Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
            await admin.PutBucketAsync(bucket);
        using var s3 = new S3BackupTarget(Settings(bucket), "test", "test");
        var test = await s3.TestAsync();
        Assert.True(test.Connected && test.WriteOk && test.ReadOk && test.DeleteOk, test.Message);

        await using var t = await TestBooks.CreateAsync();
        var svc = new BackupService(t.Clock, Path.Combine(t.Directory, "tmp"));
        var res = await svc.CreateAsync(t.Db, new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = t.DbPath },
            new LocalFileStore(Path.Combine(t.Directory, "Z")), null, new BackupOptions(new BackupKey.Passphrase("Haslo-Kopii-Chmura-1"), Path.Combine(t.Directory, "kopie")), s3);
        Assert.Null(res.RemoteError);
        Assert.StartsWith("testy/", res.RemoteKey);
        var list = await s3.ListAsync();
        Assert.Contains(list, x => x.Key == res.RemoteKey);
        var downloaded = Path.Combine(t.Directory, "pobrana.pkbak");
        await s3.DownloadAsync(res.RemoteKey!, downloaded);
        Assert.True((await svc.VerifyAsync(downloaded, new BackupKey.Passphrase("Haslo-Kopii-Chmura-1"))).Ok);
        Assert.Equal(0, await s3.ApplyRetentionAsync(5));

        using var bad = new S3BackupTarget(Settings("nieistniejacy-bucket-" + Guid.NewGuid().ToString("N")[..6]), "test", "test");
        var badTest = await bad.TestAsync();
        Assert.False(badTest.WriteOk);
        Assert.Throws<DomainException>(() => new S3BackupTarget(new ObjectStorageSettings { Endpoint = "ftp://x", Bucket = "b" }, "a", "b"));
    }
}

public class DatabaseEncryptionTests
{
    [Fact]
    public async Task Sqlite_file_is_encrypted_at_rest_and_backup_restore_still_works()
    {
        await using var t = await TestBooks.CreateAsync();
        var e = await t.Ledger().CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 10), "PK/1", new("130-01", 50m, 0), new("801", 0, 50m)));
        await t.Ledger().PostAsync(e.Id);
        var dir = Path.Combine(t.Directory, "szyfrowana");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ksiegi.db");
        File.Copy(t.DbPath, path);
        Assert.True(SqliteEncryption.IsPlaintext(path));

        var secrets = new InMemorySecretStore();
        var connector = new DatabaseConnector(secrets);
        var profile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = path };
        await using (var db = connector.CreateContext(profile))
            Assert.Equal(1, await db.JournalEntries.CountAsync());
        Assert.True(SqliteEncryption.IsEncrypted(path));                                // plik zaszyfrowany przy pierwszym otwarciu
        Assert.DoesNotContain("PK/1", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));

        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await raw.OpenAsync();
            await using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM JournalEntries";
            Assert.ThrowsAny<Exception>(() => cmd.ExecuteScalar());                  // bez klucza baza jest nieczytelna
        }
        // bez klucza na stanowisku — komunikat o odtworzeniu z kopii (nie tworzy nowej bazy w miejscu zaszyfrowanej)
        Assert.Throws<InvalidOperationException>(() => new DatabaseConnector(new InMemorySecretStore()).CreateContext(profile));

        // kopia zapasowa (archiwum szyfrowane kluczem kopii) i odtworzenie
        var store = new LocalFileStore(Path.Combine(t.Directory, "Zalaczniki"));
        var backup = new BackupService(t.Clock, Path.Combine(t.Directory, "tmp"));
        string archive;
        await using (var db = connector.CreateContext(profile))
            archive = (await backup.CreateAsync(db, profile, store, null, new BackupOptions(new BackupKey.Passphrase("Haslo-Kopii-Testowej-1"), Path.Combine(t.Directory, "kopie")))).FilePath;
        var restored = await backup.RestoreToSqliteAsync(archive, new BackupKey.Passphrase("Haslo-Kopii-Testowej-1"), Path.Combine(t.Directory, "odtworzona"));
        Assert.Empty(restored.Problems);
        var restoredProfile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = restored.DatabasePath };
        await using (var db = connector.CreateContext(restoredProfile))                 // odtworzona baza też zostaje zaszyfrowana
            Assert.Equal(1, await db.JournalEntries.CountAsync());
        Assert.True(SqliteEncryption.IsEncrypted(restored.DatabasePath));
    }

    [Fact]
    public void Remote_postgres_requires_tls()
    {
        var p = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Host = "db.firma.pl", Database = "ksiegi", Username = "ksiegowa", SslMode = PgSslMode.Disable };
        Assert.Throws<InvalidOperationException>(() => p.BuildConnectionString("x"));
        p.SslMode = PgSslMode.VerifyFull;
        Assert.Contains("db.firma.pl", p.BuildConnectionString("x"));
    }
}

public class PersonalDataTests
{
    [Fact]
    public async Task Erasure_request_is_refused_during_retention_and_anonymizes_afterwards()
    {
        await using var t = await TestBooks.CreateAsync();
        var client = await t.AddCounterpartyAsync("K001", "Jan Kowalski", "7740001454");
        await PelnaKsiegowosc.Tests.Modules.SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, client.Id, 100m, new DateOnly(2026, 1, 10));
        var svc = new Application.Security.PersonalDataService(t.Db, t.User, t.Clock);
        var refused = await svc.EraseCounterpartyAsync(client.Id, "Wniosek e-mail z 30.10.2026");
        Assert.False(refused.CanErase);                                                 // nierozliczona należność / okres przechowywania
        Assert.Equal("Jan Kowalski", (await t.Db.Counterparties.AsNoTracking().SingleAsync(c => c.Id == client.Id)).Name);

        var prospect = await t.AddCounterpartyAsync("K002", "Anna Nowak", null);
        var done = await svc.EraseCounterpartyAsync(prospect.Id, "Wniosek pisemny z 30.10.2026");
        Assert.True(done.CanErase);                                                     // brak dokumentów — dane można usunąć
        var anon = await t.Db.Counterparties.AsNoTracking().SingleAsync(c => c.Id == prospect.Id);
        Assert.StartsWith("Osoba usunięta", anon.Name);
        Assert.Null(anon.Nip);
        Assert.Equal(2, await t.Db.AuditEvents.CountAsync(a => a.Action.StartsWith("RODO_")));
    }
}
