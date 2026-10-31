using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Storage;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class PostgresSnapshotBackupTests
{
    private static readonly BackupKey Key = new BackupKey.Passphrase("Postgres-Backup-Isolation-Regression-2026");

    [PostgresFact]
    public async Task Backup_of_postgres_has_consistent_manifest_and_can_be_verified()
    {
        var (db, _, _) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var _ = db;
        await db.Database.MigrateAsync();
        var root = Path.Combine(Path.GetTempPath(), "pk-pg-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new BackupService(new FixedClock(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc)), Path.Combine(root, "staging"));
            var profile = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql };
            var files = new LocalFileStore(Path.Combine(root, "zalaczniki"));
            var result = await service.CreateAsync(db, profile, files, null,
                new BackupOptions(Key, Path.Combine(root, "archiwum"), IncludeAttachments: true));
            Assert.True(result.Verified);
            Assert.Equal("PostgreSql", result.Manifest.Engine);
            Assert.NotEmpty(result.Manifest.Tables);
            Assert.NotNull(result.Manifest.ControlSums);
            Assert.True((await service.VerifyAsync(result.FilePath, Key)).Ok);
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [PostgresFact]
    public async Task Postgres_backup_rejects_an_external_transaction_without_a_guaranteed_snapshot()
    {
        var (db, _, _) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var _ = db;
        await db.Database.MigrateAsync();
        var root = Path.Combine(Path.GetTempPath(), "pk-pg-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new BackupService(new FixedClock(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc)), Path.Combine(root, "staging"));
            await using var transaction = await db.Database.BeginTransactionAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync(db,
                    new DatabaseProfile { Engine = DatabaseEngine.PostgreSql },
                    new LocalFileStore(Path.Combine(root, "zalaczniki")), null,
                    new BackupOptions(Key, Path.Combine(root, "archiwum"), IncludeAttachments: false)));
            Assert.Contains("spójny snapshot", error.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
