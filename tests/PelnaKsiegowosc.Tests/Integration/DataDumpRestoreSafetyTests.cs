using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class DataDumpRestoreSafetyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Malformed_json_after_prior_table_import_rolls_back_and_restores_tracking(bool autoDetectChanges)
    {
        await using var source = await TestBooks.CreateAsync();
        var dump = Path.Combine(source.Directory, "dump-with-malformed-json");
        var exported = await DataDump.ExportAsync(source.Db, dump);

        // The previous test created only one JSONL file. ImportAsync rejected missing
        // tables during preflight, so it never exercised the transaction or catch/finally.
        // Corrupt the last populated table to ensure earlier populated tables were saved
        // inside the transaction before JSON deserialization fails.
        var populated = exported.Where(t => t.Rows > 0).ToArray();
        Assert.True(populated.Length >= 2, "Fixture must contain at least two populated tables.");
        await File.AppendAllTextAsync(
            Path.Combine(dump, populated[^1].Table + ".jsonl"), "{invalid-json}\n");

        await using var target = TestBooks.Open(Path.Combine(
            source.Directory, autoDetectChanges ? "rollback-enabled.db" : "rollback-disabled.db"));
        await target.Database.MigrateAsync();
        var before = await DataDump.CountsAsync(target);
        target.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => DataDump.ImportAsync(target, dump));

        Assert.Equal(autoDetectChanges, target.ChangeTracker.AutoDetectChangesEnabled);
        Assert.Null(target.Database.CurrentTransaction);
        Assert.Empty(target.ChangeTracker.Entries());
        Assert.Equal(before.OrderBy(x => x.Key),
            (await DataDump.CountsAsync(target)).OrderBy(x => x.Key));
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
