using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Regresje prawne: PIT art. 21 ust. 1 pkt 148 i SUS art. 6 ust. 4 (wiek osoby a data przychodu/okres zlecenia).</summary>
public class PayrollAgeAuditTests
{
    [Fact]
    public async Task Conflicting_pesel_and_explicit_birth_date_are_rejected()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.AddEmployeeAsync(new Employee
        {
            FirstName = "Anna", LastName = "Błędna", ContractType = ContractType.UmowaOPrace,
            Pesel = "01210112341", BirthDate = new DateOnly(2002, 1, 1),
            Gross = 6000m, StartDate = new DateOnly(2026, 1, 1)
        }));
        Assert.Contains("nie zgadza się", error.Message);
    }

    [Fact]
    public async Task Posting_youth_relief_without_verifiable_birth_date_is_blocked()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        var person = await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Ala", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            YouthRelief = true, Gross = 5000m, StartDate = new DateOnly(2026, 1, 1)
        });
        var list = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        Assert.True(list.Items.Single().ExemptRevenue > 0);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(list.Id));
        Assert.Contains("datę urodzenia", error.Message);

        var corrected = await books.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == person.Id);
        corrected.BirthDate = new DateOnly(2002, 5, 10);
        await service.UpdateEmployeeAsync(corrected);
        await service.RecalculateAsync(list.Id);
        await service.PostAsync(list.Id);
        Assert.Equal(PayrollStatus.Zaksiegowana, list.Status);
    }

    [Fact]
    public async Task Student_zlecenie_requires_verified_age_before_posting()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Student", ContractType = ContractType.UmowaZlecenie,
            StudentUnder26 = true, Gross = 2000m, ContractTotalAmount = 2000m,
            StartDate = new DateOnly(2026, 1, 1)
        });
        var list = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        Assert.Equal(0m, list.Items.Single().SocialEmployee);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(list.Id));
        Assert.Contains("datę urodzenia", error.Message);
    }

    [Fact]
    public async Task Student_turning_26_in_service_month_cannot_post_without_split_insurance_periods()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Student", ContractType = ContractType.UmowaZlecenie,
            StudentUnder26 = true, BirthDate = new DateOnly(2000, 3, 15),
            Gross = 2000m, ContractTotalAmount = 2000m, StartDate = new DateOnly(2026, 1, 1)
        });
        var list = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 20));
        var error = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(list.Id));
        Assert.Contains("26. urodzin", error.Message);
        Assert.Equal(PayrollStatus.Robocza, list.Status);
    }
}
