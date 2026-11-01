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

public class PostgresWizardTests
{
    [PostgresFact]
    public async Task Wizard_loads_at_step_two_and_finishes_without_ddl_using_restricted_operator()
    {
        using var root = new TempRoot();
        var (ownerDb, cs, databaseName) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var ownerCleanup = ownerDb;
        await ownerDb.Database.MigrateAsync();
        var role = "pk_wizard_" + Guid.NewGuid().ToString("N")[..12];
        var password = Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(cs);
        await admin.OpenAsync();
        await using (var provision = new NpgsqlCommand($"""
            CREATE ROLE "{role}" LOGIN PASSWORD '{password}';
            GRANT CONNECT ON DATABASE "{databaseName}" TO "{role}";
            GRANT USAGE ON SCHEMA public TO "{role}";
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "{role}";
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO "{role}";
            REVOKE INSERT, UPDATE, DELETE ON TABLE public."__EFMigrationsHistory" FROM "{role}";
            """, admin)) await provision.ExecuteNonQueryAsync();
        var b = new NpgsqlConnectionStringBuilder(cs);
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql, Host = b.Host, Port = b.Port,
            Database = databaseName, Username = role, SslMode = PgSslMode.Disable
        };
        var secrets = new InMemorySecretStore();
        secrets.Set(profile.PasswordSecretKey, password);
        // Reproduce the old step-10 operation with exactly the limited account:
        // EF still tries DDL although the owner already prepared the schema.
        await using (var limitedDb = DbContextFactory.Create(DatabaseEngine.PostgreSql, profile.BuildConnectionString(password)))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => limitedDb.Database.MigrateAsync());
            Assert.Equal("42501", denied.SqlState);
        }
        var session = new AppSession(new AppPaths(Path.Combine(root.Path, "first")),
            new AppConfig { ActiveDatabase = profile }, secrets, new SystemClock());
        var dialogs = new AutomaticDialogService();
        var wizard = new SetupWizardViewModel(session, dialogs) { TermsAccepted = true, SkipBackupKey = true };
        wizard.Database.Load(profile);
        try
        {
            await wizard.NextCommand.ExecuteAsync(null);
            await wizard.NextCommand.ExecuteAsync(null);
            Assert.Empty(dialogs.Errors);
            Assert.Equal(WizardStep.Jednostka, wizard.Step);
            Assert.False(wizard.ConnectToExisting);
            wizard.Company.FullName = "Testowa sp. z o.o.";
            wizard.Company.Nip = "5260250274";
            wizard.Company.Krs = "0000123456";
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

            var second = new AppSession(new AppPaths(Path.Combine(root.Path, "second")),
                new AppConfig { ActiveDatabase = profile }, secrets, new SystemClock());
            var secondDialogs = new AutomaticDialogService();
            var secondWizard = new SetupWizardViewModel(second, secondDialogs) { TermsAccepted = true, SkipBackupKey = true };
            secondWizard.Database.Load(profile);
            try
            {
                await secondWizard.NextCommand.ExecuteAsync(null);
                await secondWizard.NextCommand.ExecuteAsync(null);
                Assert.Empty(secondDialogs.Errors);
                Assert.Equal(WizardStep.Jednostka, secondWizard.Step);
                Assert.Equal("Testowa sp. z o.o.", secondWizard.Company.FullName);
                Assert.Equal("Anna", secondWizard.ManagerFirstName);
                Assert.True(secondWizard.ChartStored);
                Assert.True(secondWizard.PolicyStored);
                for (var step = 0; step < 8 && !secondWizard.IsLastStep; step++)
                {
                    await secondWizard.NextCommand.ExecuteAsync(null);
                    Assert.Empty(secondDialogs.Errors);
                }
                await secondWizard.FinishCommand.ExecuteAsync(null);
                Assert.True(secondWizard.Finished, string.Join(" | ", secondDialogs.Errors));
                Assert.Equal(1, await ownerDb.Companies.CountAsync());
                Assert.Equal(1, await ownerDb.Users.CountAsync());
                Assert.Empty(await ownerDb.Database.GetPendingMigrationsAsync());
            }
            finally { second.ReleaseOperatorSession(); }
        }
        finally
        {
            session.ReleaseOperatorSession();
            await using var cleanup = new NpgsqlCommand($"""
                REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM "{role}";
                REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM "{role}";
                REVOKE ALL PRIVILEGES ON SCHEMA public FROM "{role}";
                REVOKE ALL PRIVILEGES ON DATABASE "{databaseName}" FROM "{role}";
                DROP ROLE "{role}";
                """, admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Insufficient_privilege_message_explains_operator_rights_without_echoing_server_details()
    {
        var error = new PostgresException("secret data", "ERROR", "ERROR", "42501");
        var description = DatabaseConnector.Describe(error);
        Assert.Contains("uprawnień", description);
        Assert.Contains("42501", description);
        Assert.DoesNotContain("secret data", description);
    }
}
