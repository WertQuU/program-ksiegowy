using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Art. 92 KP: potwierdzone dni wynagrodzenia chorobowego u innych
/// pracodawców są narastające w roku kalendarzowym choroby.</summary>
public class PayrollExternalEmployerChronologyTests
{
    private static Employee Employee() => new()
    {
        FirstName = "Anna", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
        Gross = 6000m, StartDate = new DateOnly(2025, 1, 1),
        BirthDate = new DateOnly(1980, 5, 15)
    };

    [Fact]
    public async Task Later_month_cannot_reduce_previously_confirmed_external_days()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(Employee());
        var march = new DateOnly(2026, 3, 1);
        var april = new DateOnly(2026, 4, 1);
        await payroll.RecordSickWageAssessmentAsync(employee.Id, march, 30,
            SickWageCause.Ordinary80, "OSW-03");
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            payroll.RecordSickWageAssessmentAsync(employee.Id, april, 0,
                SickWageCause.Ordinary80, "OSW-04"));
        Assert.Contains("nie mogą maleć", error.Message);
        // To samo narastająco wykorzystanie jest dopuszczalne.
        await payroll.RecordSickWageAssessmentAsync(employee.Id, april, 30,
            SickWageCause.Ordinary80, "OSW-04");
    }

    [Fact]
    public async Task Earlier_month_cannot_exceed_existing_later_month_declaration()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(Employee());
        await payroll.RecordSickWageAssessmentAsync(employee.Id, new DateOnly(2026, 4, 1), 10,
            SickWageCause.Ordinary80, "OSW-04");
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            payroll.RecordSickWageAssessmentAsync(employee.Id, new DateOnly(2026, 3, 1), 20,
                SickWageCause.Ordinary80, "OSW-03"));
        Assert.Contains("Najpierw skoryguj późniejsze", error.Message);
    }

    [Fact]
    public async Task External_days_reset_in_new_calendar_year()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(Employee());
        await payroll.RecordSickWageAssessmentAsync(employee.Id, new DateOnly(2025, 12, 1), 33,
            SickWageCause.Ordinary80, "OSW-12");
        await payroll.RecordSickWageAssessmentAsync(employee.Id, new DateOnly(2026, 1, 1), 0,
            SickWageCause.Ordinary80, "OSW-01");
    }

    [Fact]
    public async Task Posting_rejects_inconsistent_old_settings_even_if_list_is_already_calculated()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(Employee());
        await payroll.RecordSickWageAssessmentAsync(employee.Id, new DateOnly(2026, 3, 1), 30,
            SickWageCause.Ordinary80, "OSW-03");

        var april = await payroll.CreateListAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        await payroll.ChangeAsync(april.Items.Single().Id, null, 5, null);
        // Sytuacja po imporcie starszych niespójnych danych: zapis omija aktualną
        // walidację formularza, ale nie może ominąć walidacji przed księgowaniem.
        var key = $"place.chorobowe.weryfikacja.{employee.Id:N}.202604";
        var oldAssessment = new SickWageAssessment(0, SickWageCause.Ordinary80,
            "STARE-USTALENIE", books.Clock.UtcNow, books.User.UserId);
        await new SettingsService(books.Db, books.Clock)
            .SetAsync(books.Company.Id, key, oldAssessment);
        await books.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<DomainException>(() => payroll.PostAsync(april.Id));
        Assert.Contains("nie mogą maleć", error.Message);
        Assert.Equal(PayrollStatus.Robocza, april.Status);
    }
}
