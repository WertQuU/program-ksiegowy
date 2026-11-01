using PelnaKsiegowosc.Application.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Security;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class PayrollPrivacyTests
{
    [Fact]
    public async Task Medical_cause_is_not_persisted_in_new_assessment()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Janina", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000, StartDate = new DateOnly(2026, 1, 1), BirthDate = new DateOnly(1985, 1, 1)
        });
        await service.RecordSickWageAssessmentAsync(employee.Id, new DateOnly(2026, 3, 1), 0, SickWageCause.Pregnancy100, "DOK-123");
        var row = await books.Db.Settings.SingleAsync(s => s.Key.StartsWith("place.chorobowe.weryfikacja."));
        Assert.DoesNotContain("Cause", row.Value);
        Assert.DoesNotContain("Pregnancy", row.Value);
        Assert.Equal(100m, JsonSerializer.Deserialize<SickWageAssessment>(row.Value)!.RatePercent);
    }

    [Fact]
    public async Task Legacy_minimization_preserves_rate_and_evidence_and_is_company_scoped()
    {
        await using var books = await TestBooks.CreateAsync();
        var legacy = new { PreviousEmployerDays = 2, Cause = 1, EvidenceReference = "DOK-123", ConfirmedAtUtc = books.Clock.UtcNow, ConfirmedByUserId = books.User.UserId };
        var key = "place.chorobowe.weryfikacja." + Guid.NewGuid().ToString("N") + ".202603";
        var settings = new SettingsService(books.Db, books.Clock);
        var otherCompany = Guid.NewGuid();
        await settings.SetAsync(books.Company.Id, key, legacy);
        await settings.SetAsync(otherCompany, key, legacy);
        await books.Db.SaveChangesAsync();
        var privacy = new PayrollPrivacyService(books.Db, books.User, books.Clock);
        Assert.Equal(1, await privacy.MinimizeLegacySettingsAsync());
        Assert.Equal(0, await privacy.MinimizeLegacySettingsAsync());
        var own = await books.Db.Settings.SingleAsync(s => s.CompanyId == books.Company.Id && s.Key == key);
        var other = await books.Db.Settings.SingleAsync(s => s.CompanyId == otherCompany && s.Key == key);
        Assert.DoesNotContain("Cause", own.Value);
        Assert.Contains("Cause", other.Value);
        var assessment = JsonSerializer.Deserialize<SickWageAssessment>(own.Value)!;
        Assert.Equal(100m, assessment.RatePercent);
        Assert.Equal(2, assessment.PreviousEmployerDays);
        Assert.Equal("DOK-123", assessment.EvidenceReference);
        Assert.True(await books.Db.AuditEvents.AnyAsync(a => a.Action == "DANE_KADROWE_ZMINIMALIZOWANE"));
    }

    [Theory]
    [InlineData(Role.Ksiegowy)]
    [InlineData(Role.Fakturzysta)]
    [InlineData(Role.Audytor)]
    [InlineData(Role.Zarzad)]
    public async Task Ordinary_accounting_roles_cannot_read_or_change_payroll(Role role)
    {
        await using var books = await TestBooks.CreateAsync();
        var user = new CurrentUser(Guid.NewGuid(), "ograniczony", books.Company.Id, new[] { role });
        await Assert.ThrowsAsync<AccessDeniedException>(() => new HrService(books.Db, user, books.Clock).AbsencesAsync(2026));
        await Assert.ThrowsAsync<AccessDeniedException>(() => new PayrollService(books.Db, user, books.Clock)
            .CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => new ReportService(books.Db, user, books.Clock).Pit11DataAsync(2026));
        await Assert.ThrowsAsync<AccessDeniedException>(() => new PayrollPrivacyService(books.Db, user, books.Clock).MinimizeLegacySettingsAsync());
    }

    [Fact]
    public async Task Dedicated_payroll_role_can_read_reports_and_read_is_audited_without_medical_details()
    {
        await using var books = await TestBooks.CreateAsync();
        var user = new CurrentUser(Guid.NewGuid(), "kadry", books.Company.Id, new[] { Role.KadryPlace });
        Assert.True(user.Can(Permission.ViewPayrollData));
        Assert.False(user.Can(Permission.ManageUsers));
        Assert.False(user.Can(Permission.ManageBackups));
        Assert.Empty(await new ReportService(books.Db, user, books.Clock).Pit11DataAsync(2026));
        var audit = await books.Db.AuditEvents.SingleAsync(a => a.Action == "RAPORT_PLACOWY_ODCZYT");
        Assert.Equal("kadry", audit.UserLogin);
        Assert.DoesNotContain("Pregnancy", audit.Details);
        Assert.DoesNotContain("100%", audit.Details);
    }

    [Fact]
    public async Task Expired_employee_retention_removes_payroll_evidence_and_absence_notes()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000, StartDate = new DateOnly(2019, 1, 1), EndDate = new DateOnly(2020, 12, 31), IsActive = false
        });
        var key = "place.chorobowe.weryfikacja." + employee.Id.ToString("N") + ".202003";
        await new SettingsService(books.Db, books.Clock).SetAsync(books.Company.Id, key, new { RatePercent = 80 });
        await new HrService(books.Db, books.User, books.Clock).AddAbsenceAsync(employee.Id, AbsenceKind.Choroba,
            new DateOnly(2020, 3, 1), new DateOnly(2020, 3, 2), "DOK-123");
        var clock = new FixedClock(new DateTime(2031, 1, 2, 12, 0, 0, DateTimeKind.Utc));
        var decision = await new PersonalDataService(books.Db, books.User, clock).EraseEmployeeAsync(employee.Id, "Wniosek DOK-456");
        Assert.True(decision.CanErase);
        Assert.False(await books.Db.Settings.AnyAsync(s => s.Key == key));
        Assert.False(await books.Db.Absences.AnyAsync(a => a.EmployeeId == employee.Id));
    }
}
