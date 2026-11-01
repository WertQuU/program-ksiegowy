using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels.Wizard;

namespace PelnaKsiegowosc.Tests.Presentation;

public class DatabaseModesWizardTests
{
    [Fact]
    public async Task Normal_session_supports_standalone_sqlite_and_reopens_books()
    {
        using var root = new TempRoot();
        var (fixture, dialogs) = SelfTestScenario.CreateSession(root.Path);
        var session = new AppSession(fixture.Paths, fixture.Config, fixture.Secrets, fixture.Clock);
        // Same public wizard commands as a normal installation, without the isolated-test bypass.
        var probe = new SetupWizardViewModel(session, dialogs) { TermsAccepted = true, CreateFirstDatabase = true };
        probe.Database.UsePostgres = false;
        await probe.NextCommand.ExecuteAsync(null);
        await probe.NextCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        Assert.True(File.Exists(probe.Database.SqlitePath));
        await SelfTestScenario.RunWizardAsync(session, dialogs);
        Assert.True((await session.OpenAsync()).Success);
        Assert.NotNull(session.Company);
        Assert.Equal(DatabaseEngine.Sqlite, session.Profile.Engine);
        Assert.True(SqliteEncryption.IsEncrypted(session.Profile.SqlitePath!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sqlite_import_in_new_or_existing_empty_target_preserves_source_and_loads_step_two(bool createNew)
    {
        using var root = new TempRoot();
        var (source, sourceDialogs) = SelfTestScenario.CreateSession(Path.Combine(root.Path, "source"));
        
        await SelfTestScenario.RunWizardAsync(source, sourceDialogs);
        var expectedId = source.Company!.Id;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var sourcePath = source.Profile.SqlitePath!;
        var original = await File.ReadAllBytesAsync(sourcePath);
        var (fixture, dialogs) = SelfTestScenario.CreateSession(Path.Combine(root.Path, "target"));
        var session = new AppSession(fixture.Paths, fixture.Config, fixture.Secrets, fixture.Clock);
        if (!createNew)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(session.Profile.SqlitePath!)!);
            await using var empty = session.Connector.CreateContext(session.Profile);
            await empty.Database.MigrateAsync();
        }
        var wizard = new SetupWizardViewModel(session, dialogs)
        {
            TermsAccepted = true, CreateFirstDatabase = createNew, MigrateFromSqlite = true,
            MigrationSourcePath = sourcePath,
            MigrationSourceKey = source.Connector.ExportSqliteKey(source.Profile)!,
            MigrationAttachmentsDirectory = source.AttachmentsRoot
        };
        wizard.Database.UsePostgres = false;
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(WizardStep.Jednostka, wizard.Step);
        Assert.True(wizard.CompanyStored);
        Assert.True(wizard.YearStored);
        Assert.True(wizard.ChartStored);
        Assert.True(wizard.ConnectToExisting);
        Assert.Contains("zweryfikowane", wizard.MigrationInfo);
        Assert.NotEmpty(Directory.GetFiles(wizard.LocalBackupDirectory));
        Assert.Equal(original, await File.ReadAllBytesAsync(sourcePath));
        wizard.RecoveryKeySaved = true;
        await wizard.FinishCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        Assert.True(wizard.Finished);
        Assert.Equal(expectedId, session.Company!.Id);
        await using var target = session.OpenDb();
        Assert.Equal(1, await target.Companies.CountAsync());
        // A repeated import into populated books must leave them intact.
        var second = new SetupWizardViewModel(session, dialogs)
        {
            MigrateFromSqlite = true, MigrationSourcePath = sourcePath,
            MigrationSourceKey = source.Connector.ExportSqliteKey(source.Profile)!,
            MigrationAttachmentsDirectory = source.AttachmentsRoot
        };
        second.Database.UsePostgres = false;
        await second.TestDatabaseCommand.ExecuteAsync(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("zawiera dane"));
        Assert.Equal(expectedId, await target.Companies.Select(c => c.Id).SingleAsync());
    }

    [Fact]
    public async Task Existing_option_does_not_silently_create_missing_sqlite_file()
    {
        using var root = new TempRoot();
        var (fixture, dialogs) = SelfTestScenario.CreateSession(root.Path);
        var session = new AppSession(fixture.Paths, fixture.Config, fixture.Secrets, fixture.Clock);
        var wizard = new SetupWizardViewModel(session, dialogs);
        wizard.Database.UsePostgres = false;
        await wizard.TestDatabaseCommand.ExecuteAsync(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("opcję A"));
        Assert.False(File.Exists(wizard.Database.SqlitePath));
    }
}

