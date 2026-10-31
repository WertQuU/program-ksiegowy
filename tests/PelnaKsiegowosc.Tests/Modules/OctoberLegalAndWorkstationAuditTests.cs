using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Infrastructure.Secrets;
using PelnaKsiegowosc.Infrastructure.Export;
using Npgsql;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Security;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class OctoberLegalAndWorkstationAuditTests
{
    [Fact]
    public void Early_invoice_does_not_advance_ordinary_vat_to_issue_month()
    {
        var invoice = new Document
        {
            Kind = DocumentKind.FakturaSprzedazy,
            IssueDate = new DateOnly(2026, 1, 20),
            OperationDate = new DateOnly(2026, 2, 10)
        };
        Assert.Equal(new DateOnly(2026, 2, 1), DocumentService.DefaultVatPeriod(invoice));
    }

    [Fact]
    public async Task Legacy_connector_refuses_sqlite_as_active_database_without_creating_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-active-pg-only-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new DatabaseProfile
            {
                Engine = DatabaseEngine.Sqlite,
                SqlitePath = Path.Combine(root, "books.sqlite")
            };
            var connector = new DatabaseConnector(new FileSecretStore(Path.Combine(root, "secrets")));
            var (opened, error) = await connector.OpenActiveAsync(profile);
            Assert.Null(opened);
            Assert.NotNull(error);
            Assert.Contains("PostgreSQL", error);
            Assert.False(File.Exists(profile.SqlitePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [PostgresFact]
    public async Task Legacy_connector_refuses_pending_postgres_migrations_without_changing_schema()
    {
        var (db, _, _) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var cleanup = db;
        var actualConnection = new NpgsqlConnectionStringBuilder(db.Database.GetDbConnection().ConnectionString);
        var credentials = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PK_TEST_PG")!);
        var root = Path.Combine(Path.GetTempPath(), "pk-no-auto-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new DatabaseProfile
            {
                Engine = DatabaseEngine.PostgreSql,
                Host = actualConnection.Host,
                Port = actualConnection.Port,
                Database = actualConnection.Database,
                Username = actualConnection.Username,
                SslMode = PgSslMode.Disable // testowy PostgreSQL na localhost
            };
            var secretStore = new FileSecretStore(Path.Combine(root, "secrets"));
            secretStore.Set(profile.PasswordSecretKey, credentials.Password ?? "");
            var connector = new DatabaseConnector(secretStore);
            var (opened, error) = await connector.OpenActiveAsync(profile);
            Assert.Null(opened);
            Assert.NotNull(error);
            Assert.Contains("Wymagana aktualizacja schematu", error);
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Employment_started_before_2019_cannot_be_erased_after_only_ten_years()
    {
        await using var t = await TestBooks.CreateAsync();
        var payroll = new PayrollService(t.Db, t.User, t.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Historyczna", LastName = "Osoba",
            ContractType = ContractType.UmowaOPrace, Gross = 3000,
            StartDate = new DateOnly(2008, 1, 1), EndDate = new DateOnly(2015, 6, 30), IsActive = false
        });
        var decision = await new PersonalDataService(t.Db, t.User, t.Clock).EmployeeRetentionAsync(employee.Id);
        Assert.False(decision.CanErase);
        Assert.Equal(new DateOnly(2065, 6, 30), decision.KeepUntil);
    }

    [Fact]
    public async Task Employment_started_after_2018_retains_to_end_of_tenth_calendar_year()
    {
        await using var t = await TestBooks.CreateAsync();
        var payroll = new PayrollService(t.Db, t.User, t.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Nowsza", LastName = "Osoba",
            ContractType = ContractType.UmowaOPrace, Gross = 3000,
            StartDate = new DateOnly(2019, 1, 1), EndDate = new DateOnly(2022, 2, 1), IsActive = false
        });
        var decision = await new PersonalDataService(t.Db, t.User, t.Clock).EmployeeRetentionAsync(employee.Id);
        Assert.False(decision.CanErase);
        Assert.Equal(new DateOnly(2032, 12, 31), decision.KeepUntil);
    }

    [Fact]
    public async Task Unknown_database_login_is_rejected_and_cannot_provision_itself()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new OperatorAccessService(t.Db, t.Clock);
        var login = "db:nieznany";
        await Assert.ThrowsAsync<DomainException>(() => service.ResolveAsync(
            OperatorAccessService.IdFor(login), login, t.Company.Id));
        Assert.False(await t.Db.Users.AnyAsync(u => u.Login == login));
    }

    [Fact]
    public async Task Operator_deactivated_by_admin_cannot_connect()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new OperatorAccessService(t.Db, t.Clock);
        var login = "db:zablokowany";
        var user = await service.RegisterAsync(t.User, login, new[] { Role.Ksiegowy });
        await service.SetAsync(t.User, user.Id, new[] { Role.Ksiegowy }, active: false);
        await Assert.ThrowsAsync<DomainException>(() =>
            service.ResolveAsync(user.Id, login, t.Company.Id));
    }

    [Fact]
    public void Postgres_identity_uses_database_login_not_os_user_or_machine()
    {
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql,
            Host = "db.example.invalid", Database = "ksiegi", Username = "ksiegowy_1"
        };
        Assert.Equal("db:ksiegowy_1", OperatorIdentity.LoginFor(profile));
        Assert.Equal(OperatorAccessService.IdFor("db:ksiegowy_1"),
            OperatorIdentity.For(Guid.NewGuid(), profile).UserId);
    }

    [Fact]
    public void Session_exclusion_lock_is_stable_for_same_database_login()
    {
        var a = ExclusivePostgresSession.LockKey("ksiegowy_1");
        Assert.Equal(a, ExclusivePostgresSession.LockKey("ksiegowy_1"));
        Assert.NotEqual(a, ExclusivePostgresSession.LockKey("ksiegowy_2"));
    }

    [PostgresFact]
    public async Task Same_postgres_login_cannot_run_two_clients_at_once_and_is_released_on_exit()
    {
        var connectionString = Environment.GetEnvironmentVariable("PK_TEST_PG")!;
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql, Host = b.Host, Port = b.Port,
            Database = b.Database, Username = b.Username
        };
        await using (var first = await ExclusivePostgresSession.AcquireAsync(profile, connectionString))
        {
            Assert.True(first.IsHealthy);
            await Assert.ThrowsAsync<DomainException>(() =>
                ExclusivePostgresSession.AcquireAsync(profile, connectionString));
            await first.EnterMaintenanceAsync();
            await using var outsider = new NpgsqlConnection(connectionString);
            await outsider.OpenAsync();
            await using var blocked = new NpgsqlCommand("SELECT pg_try_advisory_lock_shared(@key)", outsider);
            blocked.Parameters.AddWithValue("key", ExclusivePostgresSession.MaintenanceLockKey);
            Assert.False((bool)(await blocked.ExecuteScalarAsync())!);
        }
        await using var resumed = await ExclusivePostgresSession.AcquireAsync(profile, connectionString);
        Assert.True(resumed.IsHealthy);
    }

    [PostgresFact]
    public async Task Database_superuser_cannot_be_used_as_production_operator()
    {
        var cs = Environment.GetEnvironmentVariable("PK_TEST_PG")!;
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var role = new NpgsqlCommand(
            "SELECT rolsuper FROM pg_roles WHERE rolname = session_user", connection);
        if (await role.ExecuteScalarAsync() is not true)
            return; // Zewnętrzne środowiska testowe mogą udostępniać wyłącznie zwykłe konto.

        var b = new NpgsqlConnectionStringBuilder(cs);
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql,
            Host = b.Host,
            Port = b.Port,
            Database = b.Database,
            Username = b.Username
        };
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            ExclusivePostgresSession.AcquireAsync(profile, cs, requireOperatorRole: true));
        Assert.Contains("superuser", ex.Message);
    }

    [PostgresFact]
    public async Task Privileged_database_creator_is_blocked_for_operator_even_without_superuser()
    {
        var cs = Environment.GetEnvironmentVariable("PK_TEST_PG")!;
        var b = new NpgsqlConnectionStringBuilder(cs);
        await using var probe = new NpgsqlConnection(cs);
        await probe.OpenAsync();
        await using var rights = new NpgsqlCommand(
            "SELECT rolcreatedb FROM pg_roles WHERE rolname = session_user", probe);
        if (await rights.ExecuteScalarAsync() is not true)
            return; // test wymaga środowiska z uprawnieniem CREATEDB

        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql,
            Host = b.Host,
            Port = b.Port,
            Database = b.Database,
            Username = b.Username
        };
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            ExclusivePostgresSession.AcquireAsync(profile, cs, requireOperatorRole: true));
        Assert.Contains("superuser", ex.Message);
    }

    [Fact]
    public void Paper_archive_printout_is_valid_static_pdf_with_document_id()
    {
        var doc = new Document
        {
            Number = "PK/1/2026", Description = "Test wydruku",
            IssueDate = new DateOnly(2026, 10, 31), OperationDate = new DateOnly(2026, 10, 31)
        };
        var company = new Company { FullName = "Spółka testowa", Nip = "0000000000" };
        var data = PaperArchivePdf.Build(doc, company, "db:tester", DateTime.UtcNow,
            Array.Empty<Attachment>(), Array.Empty<DocumentHistory>(), new Dictionary<Guid, string>());
        Assert.True(data.Length > 200);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(data, 0, 5));
        Assert.DoesNotContain("/AcroForm", System.Text.Encoding.Latin1.GetString(data));
    }

    [Fact]
    public async Task Administrator_can_pre_register_database_login_with_narrow_roles()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new OperatorAccessService(t.Db, t.Clock);
        var login = "db:kasia";
        var registered = await service.RegisterAsync(t.User, login, new[] { Role.Ksiegowy });
        Assert.Equal(OperatorAccessService.IdFor(login), registered.Id);
        Assert.Equal(new[] { Role.Ksiegowy }, await service.ResolveAsync(registered.Id, login, t.Company.Id));
        await Assert.ThrowsAsync<DomainException>(() => service.RegisterAsync(t.User, login, new[] { Role.Administrator }));
    }

    [Fact]
    public async Task Workstation_identity_and_ambiguous_database_login_are_rejected()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new OperatorAccessService(t.Db, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() =>
            service.RegisterAsync(t.User, "accountant@LAPTOP", new[] { Role.Ksiegowy }));
        await Assert.ThrowsAsync<DomainException>(() =>
            service.RegisterAsync(t.User, "db:Kasia", new[] { Role.Ksiegowy }));
        await Assert.ThrowsAsync<DomainException>(() =>
            service.RegisterAsync(t.User, "db:pg_admin", new[] { Role.Ksiegowy }));
    }
}
