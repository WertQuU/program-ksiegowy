using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Tests.Infrastructure;

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
    [Fact]
    public async Task Validation_before_commit_rolls_back_all_imported_books()
    {
        await using var source = await TestBooks.CreateAsync();
        var dump = Path.Combine(source.Directory, "export-for-rollback");
        await DataDump.ExportAsync(source.Db, dump);
        await using var target = TestBooks.Open(Path.Combine(source.Directory, "fresh-target.db"));
        await target.Database.MigrateAsync();

        var before = await DataDump.CountsAsync(target);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DataDump.ImportAsync(target, dump, CancellationToken.None,
                (_, _) => Task.FromException(new InvalidDataException("Celowo odrzucone sumy kontrolne"))));
        Assert.Contains("Celowo odrzucone", error.Message);
        var after = await DataDump.CountsAsync(target);
        Assert.Equal(before.OrderBy(x => x.Key), after.OrderBy(x => x.Key));
        Assert.Null(target.Database.CurrentTransaction);
        Assert.Empty(target.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Incomplete_table_dump_is_rejected_without_importing_any_entity()
    {
        await using var source = await TestBooks.CreateAsync();
        var dump = Path.Combine(source.Directory, "truncated-dump");
        await DataDump.ExportAsync(source.Db, dump);
        var missing = DataDump.OrderedEntityTypes(source.Db).First().GetTableName()!;
        File.Delete(Path.Combine(dump, missing + ".jsonl"));

        await using var target = TestBooks.Open(Path.Combine(source.Directory, "partial-target.db"));
        await target.Database.MigrateAsync();
        var before = await DataDump.CountsAsync(target);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DataDump.ImportAsync(target, dump));
        Assert.Contains("Brak pliku zrzutu", ex.Message);
        Assert.Equal(before.OrderBy(x => x.Key),
            (await DataDump.CountsAsync(target)).OrderBy(x => x.Key));
    }

}
