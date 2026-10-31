using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Art. 80 KP: miesiąc 31-dniowy i 30 dni choroby mogą pozostawić
/// dzień wykonanej pracy, którego nie wolno wycenić na 0 zł.</summary>
public class PayrollThirtyOfThirtyOneDaysTests
{
    [Fact]
    public void Draft_calculation_explicitly_marks_30_of_31_sick_days_as_unverified()
    {
        var employee = new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            BirthDate = new DateOnly(1980, 1, 1)
        };
        var preview = PayrollCalculator.Compute(employee, 6000m,
            new PayrollYearToDate(), PayrollRules.Y2026, 1.67m,
            new PayrollAbsence(30, 0, 22, SickBasis: 6000m,
                CalendarDaysInMonth: 31, SickYear: 2026));
        Assert.Equal(0m, preview.SocialBasis);
        Assert.Contains("WSTĘPNA", preview.Notes);
        Assert.Contains("przepracowanych godzin", preview.Notes);
    }

    [Fact]
    public async Task Posting_30_days_in_a_31_day_month_requires_work_schedule_review()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak",
            ContractType = ContractType.UmowaOPrace, Gross = 6000m,
            BirthDate = new DateOnly(1980, 1, 1),
            StartDate = new DateOnly(2026, 1, 1)
        });
        var march = await payroll.CreateListAsync(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await payroll.ChangeAsync(march.Items.Single().Id, null, 30, null);
        Assert.Contains("WSTĘPNA", march.Items.Single().Notes);
        var error = await Assert.ThrowsAsync<DomainException>(() => payroll.PostAsync(march.Id));
        Assert.Contains("30 dniami choroby w miesiącu 31-dniowym", error.Message);
        Assert.Equal(PayrollStatus.Robocza, march.Status);
    }
}
