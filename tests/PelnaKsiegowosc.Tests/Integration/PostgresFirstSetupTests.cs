using Microsoft.EntityFrameworkCore;
using Npgsql;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels.Wizard;
using PelnaKsiegowosc.Tests.Infrastructure;
using PelnaKsiegowosc.Tests.Presentation;

namespace PelnaKsiegowosc.Tests.Integration;

public class PostgresFirstSetupTests
{
    [PostgresFact]
    public Task Wizard_creates_missing_database_schema_and_limited_operator() => RunFirstSetupAsync(false);

    [PostgresFact]
    public Task Wizard_initializes_existing_empty_database_without_manual_sql() => RunFirstSetupAsync(true);

    private static async Task RunFirstSetupAsync(bool databaseExists)
    {
        using var root = new TempRoot();
        var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PK_TEST_PG")!) { Database = "postgres", Pooling = false };
        var name = "pk_first_" + Guid.NewGuid().ToString("N")[..12];
        var login = "op_first_" + Guid.NewGuid().ToString("N")[..12];
        var password = "Operator'\\" + Guid.NewGuid().ToString("N");
        await using var server = new NpgsqlConnection(admin.ConnectionString);
        await server.OpenAsync();
        if (databaseExists)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", server);
            await create.ExecuteNonQueryAsync();
        }
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql, Host = admin.Host, Port = admin.Port,
            Database = name, Username = login, SslMode = PgSslMode.Disable
        };
        var secrets = new InMemorySecretStore();
        var session = new AppSession(new AppPaths(root.Path), new AppConfig { ActiveDatabase = profile }, secrets, new SystemClock());
        var dialogs = new AutomaticDialogService();
        var wizard = new SetupWizardViewModel(session, dialogs)
        {
            TermsAccepted = true, CreateFirstDatabase = true, AdministratorLogin = admin.Username!,
            AdministratorPassword = admin.Password!, SkipBackupKey = true
        };
        wizard.Database.Load(profile);
        wizard.Database.Password = password;
        try
        {
            await wizard.NextCommand.ExecuteAsync(null);
            await wizard.NextCommand.ExecuteAsync(null);
            Assert.Empty(dialogs.Errors);
            Assert.Equal(WizardStep.Jednostka, wizard.Step);
            Assert.False(wizard.CreateFirstDatabase);
            Assert.Empty(wizard.AdministratorPassword);
            Assert.Equal(password, secrets.Get(profile.PasswordSecretKey));
            var adminProfile = new DatabaseProfile
            {
                Engine = DatabaseEngine.PostgreSql, Host = admin.Host, Port = admin.Port,
                Database = name, Username = admin.Username
            };
            Assert.Null(secrets.Get(adminProfile.PasswordSecretKey));
            await using (var db = DbContextFactory.Create(DatabaseEngine.PostgreSql, profile.BuildConnectionString(password)))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.False(await db.Companies.AnyAsync());
                await db.Database.OpenConnectionAsync();
                await using var rights = db.Database.GetDbConnection().CreateCommand();
                rights.CommandText = "SELECT has_schema_privilege('public','CREATE') OR has_table_privilege('\"Companies\"','TRUNCATE') OR has_table_privilege('\"__EFMigrationsHistory\"','INSERT')";
                Assert.False((bool)(await rights.ExecuteScalarAsync())!);
                // The operator may append audit records but must never edit or delete history.
                rights.CommandText = "SELECT has_table_privilege('\"AuditEvents\"','UPDATE') OR has_table_privilege('\"AuditEvents\"','DELETE')";
                Assert.False((bool)(await rights.ExecuteScalarAsync())!);
                rights.CommandText = "SELECT has_table_privilege('\"AuditEvents\"','INSERT')";
                Assert.True((bool)(await rights.ExecuteScalarAsync())!);
            }
            wizard.Company.FullName = "Nowa Testowa sp. z o.o.";
            wizard.Company.Nip = "5260250274"; wizard.Company.Krs = "0000123456";
            wizard.Company.Street = "Testowa"; wizard.Company.BuildingNumber = "1";
            wizard.Company.PostalCode = "00-001"; wizard.Company.City = "Warszawa";
            wizard.Company.TaxOfficeCode = "1471";
            wizard.ManagerFirstName = "Anna"; wizard.ManagerLastName = "Nowak";
            for (var step = 0; step < 8 && !wizard.IsLastStep; step++)
            {
                await wizard.NextCommand.ExecuteAsync(null);
                Assert.Empty(dialogs.Errors);
            }
            await wizard.FinishCommand.ExecuteAsync(null);
            Assert.True(wizard.Finished, string.Join(" | ", dialogs.Errors));
            session.ReleaseOperatorSession();
            var blocked = await Assert.ThrowsAsync<DomainException>(() =>
                PostgresFirstSetup.PrepareAsync(profile, admin.Username!, admin.Password!, password));
            Assert.Contains("już tabele", blocked.Message);
            await using var check = DbContextFactory.Create(DatabaseEngine.PostgreSql, profile.BuildConnectionString(password));
            Assert.Equal(1, await check.Companies.CountAsync());
            Assert.Equal(1, await check.Users.CountAsync());
        }
        finally
        {
            session.ReleaseOperatorSession();
            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new NpgsqlCommand();
            cleanup.Connection = server;
            // DROP DATABASE must be a standalone command, outside an implicit batch transaction.
            cleanup.CommandText = $"DROP DATABASE IF EXISTS \"{name}\"";
            await cleanup.ExecuteNonQueryAsync();
            cleanup.CommandText = $"DROP ROLE IF EXISTS \"{login}\"";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Wizard_clears_administrator_password_when_setup_fails()
    {
        using var root = new TempRoot();
        var (session, dialogs) = SelfTestScenario.CreateSession(root.Path);
        var wizard = new SetupWizardViewModel(session, dialogs)
        {
            AdministratorLogin = "postgres", AdministratorPassword = "admin-secret", CreateFirstDatabase = true
        };
        wizard.Database.UsePostgres = true; wizard.Database.Host = "127.0.0.1"; wizard.Database.Port = "1";
        wizard.Database.Database = "setup_test"; wizard.Database.Username = "operator"; wizard.Database.Password = "operator-secret";
        wizard.Database.SslMode = wizard.Database.SslModes.First(s => s.Value == PgSslMode.Disable);
        await wizard.PrepareFirstDatabaseCommand.ExecuteAsync(null);
        Assert.Empty(wizard.AdministratorPassword);
        Assert.NotEmpty(dialogs.Errors);
        Assert.DoesNotContain(dialogs.Errors, e => e.Contains("admin-secret") || e.Contains("operator-secret"));
    }

    [Fact]
    public async Task First_setup_rejects_system_database_and_same_admin_operator_before_connecting()
    {
        var profile = new DatabaseProfile { Engine = DatabaseEngine.PostgreSql, Database = "postgres", Username = "operator" };
        await Assert.ThrowsAsync<DomainException>(() => PostgresFirstSetup.PrepareAsync(profile, "admin", "admin-secret", "op-secret"));
        profile.Database = "books";
        await Assert.ThrowsAsync<DomainException>(() => PostgresFirstSetup.PrepareAsync(profile, "operator", "admin-secret", "op-secret"));
    }
}
