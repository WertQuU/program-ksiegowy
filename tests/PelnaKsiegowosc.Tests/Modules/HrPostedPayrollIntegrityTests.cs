using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class HrPostedPayrollIntegrityTests
{
    [Fact]
    public async Task Posted_payroll_prevents_adding_or_removing_absences_in_same_month()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var hr = new HrService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(2026, 1, 1)
        });
        var day = new DateOnly(2026, 1, 5);
        var original = await hr.AddAbsenceAsync(employee.Id, AbsenceKind.UrlopWypoczynkowy, day, day);
        var list = await payroll.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await payroll.PostAsync(list.Id);
        Assert.Equal(PayrollStatus.Zaksiegowana, list.Status);

        var add = await Assert.ThrowsAsync<DomainException>(() => hr.AddAbsenceAsync(
            employee.Id, AbsenceKind.UrlopWypoczynkowy, day.AddDays(1), day.AddDays(1)));
        Assert.Contains("zaksięgowana lista płac", add.Message);
        var remove = await Assert.ThrowsAsync<DomainException>(() => hr.DeleteAbsenceAsync(original.Id));
        Assert.Contains("zaksięgowana lista płac", remove.Message);
        Assert.True(await books.Db.Absences.AsNoTracking().AnyAsync(a => a.Id == original.Id));
        Assert.Equal(1, await books.Db.Absences.CountAsync(a => a.CompanyId == books.Company.Id));

        // Kolejny miesiąc bez zaksięgowanej listy można nadal korygować.
        var nextMonth = new DateOnly(2026, 2, 2);
        var future = await hr.AddAbsenceAsync(employee.Id, AbsenceKind.UrlopWypoczynkowy, nextMonth, nextMonth);
        await hr.DeleteAbsenceAsync(future.Id);
        Assert.False(await books.Db.Absences.AnyAsync(a => a.Id == future.Id));
    }

    [Fact]
    public async Task Multi_month_absence_is_blocked_if_any_month_has_posted_payroll()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var hr = new HrService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Ola", LastName = "Kowal", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(2026, 1, 1)
        });
        var january = await payroll.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await payroll.PostAsync(january.Id);

        // Nawet gdy początek absencji jest w niezaksiegowanym lutym,
        // okres zachodzący na zaksięgowany styczeń nie może ominąć blokady.
        var error = await Assert.ThrowsAsync<DomainException>(() => hr.AddAbsenceAsync(
            employee.Id, AbsenceKind.UrlopBezplatny,
            new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 2)));
        Assert.Contains("zaksięgowana lista płac", error.Message);
    }

    [Fact]
    public async Task Absence_cannot_precede_employment_or_follow_contract_end()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var hr = new HrService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Maria", LastName = "Test", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(2026, 2, 1), EndDate = new DateOnly(2026, 2, 28)
        });
        var before = await Assert.ThrowsAsync<DomainException>(() => hr.AddAbsenceAsync(employee.Id,
            AbsenceKind.UrlopBezplatny, new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 2)));
        var after = await Assert.ThrowsAsync<DomainException>(() => hr.AddAbsenceAsync(employee.Id,
            AbsenceKind.UrlopBezplatny, new DateOnly(2026, 2, 27), new DateOnly(2026, 3, 1)));
        Assert.Contains("okresie zatrudnienia", before.Message);
        Assert.Contains("okresie zatrudnienia", after.Message);
        Assert.False(await books.Db.Absences.AnyAsync());
    }
}
