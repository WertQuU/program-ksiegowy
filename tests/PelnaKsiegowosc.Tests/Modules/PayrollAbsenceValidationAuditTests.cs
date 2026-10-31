using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class PayrollAbsenceValidationAuditTests
{
    private static Employee Worker() => new()
    {
        FirstName = "Jan", LastName = "Testowy",
        ContractType = ContractType.UmowaOPrace,
        Gross = 6000m, StartDate = new DateOnly(2026, 1, 1)
    };

    [Theory]
    [InlineData(-1, 0, 80)]
    [InlineData(0, -1, 80)]
    [InlineData(0, 21, 80)]
    [InlineData(2, 0, 70)]
    [InlineData(2, 0, 110)]
    public void Calculator_rejects_invalid_absence_even_if_both_counts_are_nonpositive(
        int sickDays, int unpaidDays, int rate)
    {
        Assert.Throws<DomainException>(() =>
            PayrollCalculator.Compute(Worker(), 6000m, new PayrollYearToDate(),
                PayrollRules.Y2026, 1.67m,
                new PayrollAbsence(sickDays, unpaidDays, 20,
                    SickBasis: 6000m, SickRatePercent: rate)));
    }

    [Fact]
    public async Task Changing_sick_days_to_negative_never_saves_invalid_payroll_item()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        await payroll.AddEmployeeAsync(Worker());
        var list = await payroll.CreateListAsync(
            new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        var item = list.Items.Single();
        await Assert.ThrowsAsync<DomainException>(() =>
            payroll.ChangeAsync(item.Id, null, -1, null));
        Assert.Equal(0, item.SickDays);
        Assert.Equal(PayrollStatus.Robocza, list.Status);
    }
}
