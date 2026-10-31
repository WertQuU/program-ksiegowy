using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
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

            await using (var local = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = mirror.FilePath }.ToString()))
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
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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
