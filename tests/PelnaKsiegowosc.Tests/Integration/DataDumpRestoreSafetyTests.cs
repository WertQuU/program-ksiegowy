using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Backup;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class DataDumpRestoreSafetyTests
{
    [Fact]
    public async Task Failed_import_restores_original_change_tracking_setting()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = DbContextFactory.Create(
                DatabaseEngine.Sqlite, new DatabaseProfile
                {
                    Engine = DatabaseEngine.Sqlite,
                    SqlitePath = Path.Combine(root, "empty.db")
                }.BuildConnectionString(null));
            await db.Database.MigrateAsync();

            var dump = Path.Combine(root, "dump");
            Directory.CreateDirectory(dump);
            var first = DataDump.OrderedEntityTypes(db).First().GetTableName()!;
            await File.WriteAllTextAsync(Path.Combine(dump, first + ".jsonl"), "{invalid-json}");

            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            await Assert.ThrowsAnyAsync<Exception>(() => DataDump.ImportAsync(db, dump));
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
