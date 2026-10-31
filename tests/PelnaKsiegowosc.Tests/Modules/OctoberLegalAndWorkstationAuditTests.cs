using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Infrastructure.Configuration;
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
