using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
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
    private static readonly byte[] AttKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Backup_path_validation_rejects_sibling_directory_prefix_and_parent_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "pkbak-test", "zawartosc");
        Assert.True(BackupService.IsPathInsideBackupRoot(root, Path.Combine("baza", "ksiegi.db")));
        Assert.False(BackupService.IsPathInsideBackupRoot(root, Path.Combine("..", "zawartosc-inne", "secret.txt")));
        Assert.False(BackupService.IsPathInsideBackupRoot(root, Path.Combine("..", "obcy.txt")));
        Assert.False(BackupService.IsPathInsideBackupRoot(root, Path.Combine(Path.GetPathRoot(root)!, "secret.txt")));
    }

    internal static async Task<(TestBooks t, LocalFileStore store)> BooksWithAttachmentAsync()
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
        var dbKey = SqliteEncryption.NewKey();
        await Assert.ThrowsAsync<DomainException>(() => svc.RestoreToSqliteAsync(res.FilePath, Pass, restoreDir, "", AttKey)); // bez klucza — odmowa
        var restored = await svc.RestoreToSqliteAsync(res.FilePath, Pass, restoreDir + Path.DirectorySeparatorChar, dbKey, AttKey);
        Assert.True(restored.Ok, string.Join("; ", restored.Problems));
        Assert.True(SqliteEncryption.IsEncrypted(restored.DatabasePath));
        await using var db2 = DbContextFactory.Create(DatabaseEngine.Sqlite,
            new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = restored.DatabasePath }.BuildConnectionString(null, encrypted: true), dbKey);
        var tbOrig = await new LedgerQueries(t.Db).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        var tbRest = await new LedgerQueries(db2).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        Assert.Equal(tbOrig.Select(x => (x.Account.Number, x.Balance)), tbRest.Select(x => (x.Account.Number, x.Balance)));
        var integrity = await new IntegrityService(db2, t.Clock).FullCheckAsync(t.Company.Id);
        Assert.True(integrity.AllOk, string.Join("; ", integrity.Results.Where(r => !r.Ok).Select(r => r.Details)));
        var att = await db2.Attachments.SingleAsync();
        var bytes = await new DocumentService(db2, t.User, t.Clock).ReadAttachmentAsync(att.Id, new LocalFileStore(restored.AttachmentsPath, AttKey));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes));
        // odtworzone załączniki zapisane na dysku wyłącznie w postaci zaszyfrowanej
        foreach (var f in Directory.EnumerateFiles(restored.AttachmentsPath, "*.bin", SearchOption.AllDirectories))
            Assert.True(AttachmentCrypto.IsEncrypted(File.ReadAllBytes(f)));
        await Assert.ThrowsAsync<DomainException>(() => new DocumentService(db2, t.User, t.Clock).ReadAttachmentAsync(att.Id, new LocalFileStore(restored.AttachmentsPath)));

        // ponowne odtworzenie do tego samego katalogu — odmowa (brak nadpisania)
        await Assert.ThrowsAsync<DomainException>(() => svc.RestoreToSqliteAsync(res.FilePath, Pass, restoreDir, dbKey, AttKey));
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
        await Assert.ThrowsAnyAsync<Exception>(() => svc.RestoreToSqliteAsync(tampered, Pass, Path.Combine(t.Directory, "x"), SqliteEncryption.NewKey(), AttKey));
        Assert.False(File.Exists(Path.Combine(t.Directory, "x", "ksiegi.db")));
    }

    [Fact]
    public async Task Malformed_archive_header_cannot_control_kdf_workload_or_chunk_allocation()
    {
        var key = new BackupKey.RawKey(RandomNumberGenerator.GetBytes(32));
        using var encrypted = new MemoryStream();
        await ArchiveCrypto.EncryptAsync(new MemoryStream("test"u8.ToArray()), encrypted, key, Guid.NewGuid(), DateTime.UtcNow);
        var source = encrypted.ToArray();

        static byte[] RewriteHeader(byte[] bytes, Action<JsonObject> modify)
        {
            var oldLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
            var json = JsonNode.Parse(Encoding.UTF8.GetString(bytes.AsSpan(12, oldLength)))!.AsObject();
            modify(json);
            var replacement = Encoding.UTF8.GetBytes(json.ToJsonString());
            var result = new byte[12 + replacement.Length + bytes.Length - 12 - oldLength];
            bytes.AsSpan(0, 8).CopyTo(result);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), replacement.Length);
            replacement.CopyTo(result.AsSpan(12));
            bytes.AsSpan(12 + oldLength).CopyTo(result.AsSpan(12 + replacement.Length));
            return result;
        }

        // Reject absurd workload and allocation sizes BEFORE any unauthenticated values are used.
        foreach (var altered in new[]
        {
            RewriteHeader(source, h => h["Iterations"] = int.MaxValue),
            RewriteHeader(source, h => h["ChunkSize"] = int.MaxValue),
            RewriteHeader(source, h => h["Version"] = 2),
            RewriteHeader(source, h => h["NoncePrefix"] = "***not-base64***"),
            RewriteHeader(source, h => h["KeyMode"] = "passphrase-pbkdf2-sha256")
        })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ArchiveCrypto.DecryptAsync(new MemoryStream(altered), new MemoryStream(), key));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ArchiveCrypto.ReadHeaderAsync(new MemoryStream(altered)));
        }

        // An incomplete length field or a shortened JSON header must fail closed.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveCrypto.DecryptAsync(new MemoryStream(source[..9]), new MemoryStream(), key));
        var cut = source[..(12 + BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(8, 4)) - 1)];
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveCrypto.DecryptAsync(new MemoryStream(cut), new MemoryStream(), key));
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
        // Przygotowanie schematu jest obowiązkiem administratora/testu, nie OpenActiveAsync.
        // Aktywny klient nie może wykonywać automatycznych migracji istniejącej bazy.
        await pg.Database.MigrateAsync();
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
    public void Encrypted_export_is_fully_verified_and_corruption_or_wrong_key_is_detected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pk-eksport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "jawna.db");
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={src};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE JournalEntries(Id TEXT PRIMARY KEY, Number TEXT, Amount TEXT); CREATE INDEX ix ON JournalEntries(Number);";
                cmd.ExecuteNonQuery();
                for (var i = 0; i < 2000; i++)
                {
                    cmd.CommandText = $"INSERT INTO JournalEntries VALUES('{Guid.NewGuid()}','PK/{i}','{i * 1.23m}')";
                    cmd.ExecuteNonQuery();
                }
            }
            var key = SqliteEncryption.NewKey();
            var enc = Path.Combine(dir, "zaszyfrowana.db");
            SqliteEncryption.ExportEncrypted(src, enc, key);
            Assert.True(SqliteEncryption.IsEncrypted(enc));
            Assert.DoesNotContain("PK/1999", Encoding.UTF8.GetString(File.ReadAllBytes(enc)));
            SqliteEncryption.VerifyEncryptedCopy(src, enc, key);                                   // ponowna weryfikacja — zgodna
            Assert.ThrowsAny<Exception>(() => SqliteEncryption.VerifyEncryptedCopy(src, enc, SqliteEncryption.NewKey())); // inny klucz

            // uszkodzenie jednej strony wykrywane przez HMAC SQLCipher
            var bytes = File.ReadAllBytes(enc);
            bytes[bytes.Length - 100] ^= 0x5A;
            var broken = Path.Combine(dir, "uszkodzona.db");
            File.WriteAllBytes(broken, bytes);
            Assert.ThrowsAny<Exception>(() => SqliteEncryption.VerifyEncryptedCopy(src, broken, key));

            // różnica treści względem źródła wykrywana (odcisk tabel)
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={src};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE JournalEntries SET Amount='0' WHERE Number='PK/5'";
                cmd.ExecuteNonQuery();
            }
            var ex = Assert.Throws<InvalidOperationException>(() => SqliteEncryption.VerifyEncryptedCopy(src, enc, key));
            Assert.Contains("JournalEntries", ex.Message);
            Assert.Throws<InvalidOperationException>(() => SqliteEncryption.ExportEncrypted(src, enc, key)); // brak nadpisania
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }

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
        var restoredProfile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = Path.Combine(t.Directory, "odtworzona", "ksiegi.db") };
        var restored = await backup.RestoreToSqliteAsync(archive, new BackupKey.Passphrase("Haslo-Kopii-Testowej-1"), Path.Combine(t.Directory, "odtworzona"),
            databaseKey: connector.SqliteKeyForNewFile(restoredProfile), attachmentsKey: RandomNumberGenerator.GetBytes(32));
        Assert.Empty(restored.Problems);
        Assert.True(SqliteEncryption.IsEncrypted(restored.DatabasePath));                 // zaszyfrowana od razu po odtworzeniu
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(t.Directory, "tmp")));   // brak jawnych plików roboczych
        await using (var db = connector.CreateContext(restoredProfile))
            Assert.Equal(1, await db.JournalEntries.CountAsync());
    }

    [Fact]
    public void Remote_postgres_requires_tls()
    {
        var p = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Host = "db.firma.pl", Database = "ksiegi", Username = "ksiegowa", SslMode = PgSslMode.Disable };
        foreach (var mode in new[] { PgSslMode.Disable, PgSslMode.Prefer, PgSslMode.Require, PgSslMode.VerifyCA })
        {
            p.SslMode = mode;
            Assert.Throws<InvalidOperationException>(() => p.BuildConnectionString("x"));
        }
        p.SslMode = PgSslMode.VerifyFull;
        Assert.Contains("db.firma.pl", p.BuildConnectionString("x"));
    }
}

public class AttachmentEncryptionTests
{
    [Fact]
    public async Task Existing_evidence_cannot_be_overwritten_by_a_second_attachment_write()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pk-immutable-attachment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileStore(dir, RandomNumberGenerator.GetBytes(32));
            var first = "%PDF-1.7 oryginal";
            await store.SaveAtAsync("2026/10/dowod.bin", new MemoryStream(Encoding.UTF8.GetBytes(first)));
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.SaveAtAsync("2026/10/dowod.bin",
                    new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7 podmieniony"))));
            await using var stored = await store.OpenAsync("2026/10/dowod.bin");
            using var output = new MemoryStream();
            await stored.CopyToAsync(output);
            Assert.Equal(first, Encoding.UTF8.GetString(output.ToArray()));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Attachments_are_encrypted_at_rest_and_legacy_files_are_converted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pk-zal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var store = new LocalFileStore(dir, key);
            var plain = Encoding.UTF8.GetBytes("%PDF-1.7 Faktura VAT nr FV/1/2026 NIP 5260250274 " + new string('x', 3 * 1024 * 1024)); // wiele fragmentów
            var (sha, size) = await store.SaveAtAsync("a/plik.bin", new MemoryStream(plain));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(plain)), sha, ignoreCase: true);
            Assert.Equal(plain.Length, size);
            var onDisk = await File.ReadAllBytesAsync(Path.Combine(dir, "a", "plik.bin"));
            Assert.True(AttachmentCrypto.IsEncrypted(onDisk));
            Assert.DoesNotContain("Faktura", Encoding.UTF8.GetString(onDisk));
            await using (var s = await store.OpenAsync("a/plik.bin"))
            {
                using var ms = new MemoryStream(); await s.CopyToAsync(ms);
                Assert.Equal(plain, ms.ToArray());
            }
            // bez klucza lub z innym kluczem — brak dostępu
            await Assert.ThrowsAsync<DomainException>(() => new LocalFileStore(dir).OpenAsync("a/plik.bin"));
            await Assert.ThrowsAnyAsync<Exception>(() => new LocalFileStore(dir, RandomNumberGenerator.GetBytes(32)).OpenAsync("a/plik.bin"));
            // modyfikacja treści wykrywana (AES-GCM)
            onDisk[onDisk.Length / 2] ^= 1;
            await File.WriteAllBytesAsync(Path.Combine(dir, "a", "plik.bin"), onDisk);
            await Assert.ThrowsAnyAsync<Exception>(() => store.OpenAsync("a/plik.bin"));
            Assert.Contains("a/plik.bin", store.Check().Unreadable);                 // pełna kontrola magazynu wykrywa zmieniony plik
            File.Delete(Path.Combine(dir, "a", "plik.bin"));

            // magazyn zaszyfrowany od początku: bez klucza nie zapisuje; plik z uszkodzonym nagłówkiem — uszkodzony, nie „jawny”
            Assert.True(LocalFileStore.EncryptedOnly(dir));
            await Assert.ThrowsAsync<DomainException>(() => new LocalFileStore(dir).SaveAtAsync("c/nowy.bin", new MemoryStream(new byte[] { 1 })));
            await store.SaveAtAsync("c/dobry.bin", new MemoryStream("dokument"u8.ToArray()));
            var damaged = await File.ReadAllBytesAsync(Path.Combine(dir, "c", "dobry.bin"));
            damaged[0] = (byte)'X';
            await File.WriteAllBytesAsync(Path.Combine(dir, "c", "zly.bin"), damaged);
            await Assert.ThrowsAsync<DomainException>(() => store.OpenAsync("c/zly.bin"));
            Assert.Contains("c/zly.bin", store.Check().Unreadable);
            Assert.Equal(0, await store.EncryptLegacyFilesAsync());                                  // uszkodzony plik nie jest „szyfrowany jako jawny”
            File.Delete(Path.Combine(dir, "c", "zly.bin"));
            Assert.True(LocalFileStore.KeyMatches(dir, key));
            Assert.False(LocalFileStore.KeyMatches(dir, RandomNumberGenerator.GetBytes(32)));
            Assert.True(LocalFileStore.KeyMatches(Path.Combine(dir, "brak"), RandomNumberGenerator.GetBytes(32)));
            await Assert.ThrowsAsync<DomainException>(() => new LocalFileStore(dir, null, keyRequired: true).SaveAtAsync("c/nowy.bin", new MemoryStream(new byte[] { 1 })));

            // dotychczasowe jawne pliki (katalog z wcześniejszej wersji) są szyfrowane przy otwarciu firmy, po czym katalog jest oznaczany
            var legacyDir = Path.Combine(dir, "..", Path.GetFileName(dir) + "-stary");
            try
            {
                await new LocalFileStore(legacyDir).SaveAtAsync("b/stary.bin", new MemoryStream("jawny dokument"u8.ToArray()));
                var legacy = new LocalFileStore(legacyDir, key);
                Assert.False(LocalFileStore.EncryptedOnly(legacyDir));
                Assert.Equal(1, await legacy.EncryptLegacyFilesAsync());
                Assert.True(LocalFileStore.EncryptedOnly(legacyDir));
                Assert.True(AttachmentCrypto.IsEncrypted(await File.ReadAllBytesAsync(Path.Combine(legacyDir, "b", "stary.bin"))));
                await using var r = await legacy.OpenAsync("b/stary.bin");
                Assert.Equal("jawny dokument", await new StreamReader(r).ReadToEndAsync());
            }
            finally { try { Directory.Delete(legacyDir, true); } catch { } }

            // plik zaszyfrowany innym kluczem w tym samym katalogu — wykrywany przez pełną kontrolę (nie tylko pierwszy plik)
            await new LocalFileStore(dir, RandomNumberGenerator.GetBytes(32)).SaveAtAsync("z/obcy.bin", new MemoryStream("inny klucz"u8.ToArray()));
            Assert.False(LocalFileStore.KeyMatches(dir, key));
            var check = store.Check();
            Assert.False(check.Ok);
            Assert.Contains("z/obcy.bin", check.Unreadable);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class BackupExtractionLimitTests
{
    [Fact]
    public async Task Restore_stops_when_expanded_size_exceeds_limit_and_leaves_no_temporary_data()
    {
        var (t, store) = await BackupTests.BooksWithAttachmentAsync();
        await using var _ = t;
        var tmp = Path.Combine(t.Directory, "tmp");
        var svc = new BackupService(t.Clock, tmp);
        var key = new BackupKey.Passphrase("Bardzo-Tajne-Haslo-Kopii-2026");
        var res = await svc.CreateAsync(t.Db, new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = Path.Combine(t.Directory, "ksiegi.db") }, store, null, new BackupOptions(key, Path.Combine(t.Directory, "kopie")));
        var limited = new BackupService(t.Clock, tmp) { MaxExpansionFloor = 1024, MaxExpansionRatio = 1 };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => limited.RestoreToSqliteAsync(res.FilePath, key, Path.Combine(t.Directory, "x"), SqliteEncryption.NewKey(), RandomNumberGenerator.GetBytes(32)));
        Assert.Contains("przekracza dopuszczalny rozmiar", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp));
        var fewFiles = new BackupService(t.Clock, tmp) { MaxArchiveEntries = 1 };
        var v = await fewFiles.VerifyAsync(res.FilePath, key);
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Contains("plików"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp));

        // pełna kontrola integralności: brakujący i zmieniony załącznik są wykazywane
        var report = await new IntegrityService(t.Db, t.Clock).FullCheckAsync(t.Company.Id, attachments: store);
        Assert.True(report.Results.Single(r => r.Area.StartsWith("Załączniki")).Ok);
        var att = await t.Db.Attachments.SingleAsync();
        File.Delete(Path.Combine(store.RootPath, att.StoragePath));
        report = await new IntegrityService(t.Db, t.Clock).FullCheckAsync(t.Company.Id, attachments: store);
        var row = report.Results.Single(r => r.Area.StartsWith("Załączniki"));
        Assert.False(row.Ok);
        Assert.Contains("brak pliku", row.Details);
    }
}

public class AttachmentCryptoTamperTests
{
    [Fact]
    public async Task Streaming_encryption_handles_chunk_boundaries_and_matches_hash()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        foreach (var len in new[] { 0, 1, 1024 * 1024 - 1, 1024 * 1024, 1024 * 1024 + 1, 2 * 1024 * 1024 })
        {
            var plain = RandomNumberGenerator.GetBytes(len);
            using var enc = new MemoryStream();
            var (sha, size) = await AttachmentCrypto.EncryptStreamAsync(new MemoryStream(plain), enc, key);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(plain)), sha);
            Assert.Equal(len, size);
            Assert.Equal(plain, AttachmentCrypto.Decrypt(enc.ToArray(), key));
        }
    }

    [Fact]
    public async Task Header_only_truncated_reordered_and_forged_files_are_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        async Task<byte[]> Enc(byte[] plain) { using var ms = new MemoryStream(); await AttachmentCrypto.EncryptAsync(plain, ms, key); return ms.ToArray(); }
        var big = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 10);   // 3 porcje
        var data = await Enc(big);
        Assert.Equal(big, AttachmentCrypto.Decrypt(data, key));
        Assert.Empty(AttachmentCrypto.Decrypt(await Enc(Array.Empty<byte>()), key));          // pusty załącznik — jedna uwierzytelniona porcja

        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(data[..16], key));                          // sam nagłówek
        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(data[..20], key));                          // nagłówek + długość
        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(data[..^1], key));                          // ucięty tag
        var chunk = 4 + 1024 * 1024 + 16;
        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(data[..(16 + chunk)], key));               // obcięty na granicy porcji
        var badTag = (byte[])data.Clone(); badTag[^1] ^= 1;
        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(badTag, key));                               // fałszywy tag
        var badLen = (byte[])data.Clone(); BitConverter.GetBytes(-5).CopyTo(badLen, 16);
        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(badLen, key));                               // nieprawidłowa długość
        var swapped = (byte[])data.Clone();                                                                        // zamiana porcji 1 i 2
        Array.Copy(data, 16 + chunk, swapped, 16, chunk);
        Array.Copy(data, 16, swapped, 16 + chunk, chunk);
        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(swapped, key));
        Assert.Throws<DomainException>(() => AttachmentCrypto.Decrypt(data, RandomNumberGenerator.GetBytes(32))); // inny klucz
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
        await svc.SetLegalHoldAsync(prospect.Id, new DateOnly(2030, 12, 31), "Kontrola podatkowa — zawieszenie biegu przedawnienia");
        Assert.False((await svc.CounterpartyRetentionAsync(prospect.Id)).CanErase);                   // wstrzymanie usuwania
        await svc.SetLegalHoldAsync(prospect.Id, new DateOnly(2020, 1, 1), "Kontrola zakończona");
        var done = await svc.EraseCounterpartyAsync(prospect.Id, "Wniosek pisemny z 30.10.2026");
        Assert.True(done.CanErase);                                                     // brak dokumentów — dane można usunąć
        var anon = await t.Db.Counterparties.AsNoTracking().SingleAsync(c => c.Id == prospect.Id);
        Assert.StartsWith("Osoba usunięta", anon.Name);
        Assert.Null(anon.Nip);
        Assert.Equal(4, await t.Db.AuditEvents.CountAsync(a => a.Action.StartsWith("RODO_")));
    }
}
