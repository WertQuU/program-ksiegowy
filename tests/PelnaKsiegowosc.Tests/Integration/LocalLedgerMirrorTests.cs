using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Infrastructure.Integrity;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class LocalLedgerMirrorTests
{
    [PostgresFact]
    public async Task Baseline_is_created_only_once_and_missing_known_entry_is_reported_without_a_write_to_postgres()
    {
        var (db, _, _) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var cleanup = db;
        await db.Database.MigrateAsync();
        var companyId = Guid.NewGuid();
        db.Companies.Add(new Company
        {
            Id = companyId,
            FullName = "Testowa P.S.A.",
            Nip = "5260250274",
            Street = "Testowa",
            BuildingNumber = "1",
            PostalCode = "00-001",
            City = "Warszawa"
        });
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "pk-local-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new DatabaseProfile
            {
                Engine = DatabaseEngine.PostgreSql,
                Host = "127.0.0.1",
                Database = "local-integrity-test"
            };
            var mirror = new LocalLedgerMirror(root, profile, companyId);
            var first = await mirror.CompareAsync(db, companyId);
            Assert.Equal(LocalLedgerMirror.MirrorStatus.BaselineCreated, first.Status);
            Assert.True(File.Exists(mirror.FilePath));
            var second = await mirror.CompareAsync(db, companyId);
            Assert.Equal(LocalLedgerMirror.MirrorStatus.Synchronized, second.Status);
            Assert.Null(db.Database.CurrentTransaction);

            await using (var local = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = mirror.FilePath, Pooling = false }.ToString()))
            {
                await local.OpenAsync();
                await using var insert = local.CreateCommand();
                insert.CommandText = "INSERT INTO ledger_snapshot(entry_id,fiscal_year_id,sequence_no,digest) VALUES($id,$year,1,'BAD')";
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue("$year", Guid.NewGuid().ToString("N"));
                await insert.ExecuteNonQueryAsync();
            }

            var mismatched = await mirror.CompareAsync(db, companyId);
            Assert.Equal(LocalLedgerMirror.MirrorStatus.Divergence, mismatched.Status);
            Assert.Contains(mismatched.Problems, p => p.Contains("Usunięto zaksięgowany zapis"));
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(0, await db.JournalEntries.CountAsync());
        }
        finally
        {
            // CompareAsync uses pooled SQLite connections. Release idle handles
            // before removing the temporary directory on Windows.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [PostgresFact]
    public async Task Missing_company_does_not_create_a_false_integrity_baseline()
    {
        var (db, _, _) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var cleanup = db;
        await db.Database.MigrateAsync();
        var companyId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "pk-missing-company-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new DatabaseProfile
            {
                Engine = DatabaseEngine.PostgreSql,
                Host = "127.0.0.1",
                Database = "missing-company-test"
            };
            var mirror = new LocalLedgerMirror(root, profile, companyId);
            var result = await mirror.CompareAsync(db, companyId);
            Assert.Equal(LocalLedgerMirror.MirrorStatus.Divergence, result.Status);
            Assert.Contains(result.Problems, p => p.Contains("Brak firmy"));
            Assert.False(File.Exists(mirror.FilePath));
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Checkpoint_of_one_company_cannot_be_used_to_check_another_company()
    {
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql,
            Host = "127.0.0.1",
            Database = "multi-company-check"
        };
        var firstCompany = Guid.NewGuid();
        var mirror = new LocalLedgerMirror("temp", profile, firstCompany);
        await using var db = DbContextFactory.Create(DatabaseEngine.Sqlite, "Data Source=:memory:");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mirror.CompareAsync(db, Guid.NewGuid()));
        Assert.Contains("innej firmy", ex.Message);
    }

    [Fact]
    public void Two_server_profiles_have_distinct_local_checkpoints()
    {
        var companyId = Guid.NewGuid();
        var p = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Host = "server-a", Database = "books" };
        var q = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Host = "server-b", Database = "books" };
        Assert.NotEqual(new LocalLedgerMirror("temp", p, companyId).FilePath,
            new LocalLedgerMirror("temp", q, companyId).FilePath);
        Assert.Throws<InvalidOperationException>(() => new LocalLedgerMirror("temp",
            new DatabaseProfile { Engine = DatabaseEngine.Sqlite }, companyId));
    }
}
