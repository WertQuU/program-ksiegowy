using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Art. 92 KP: 33/14 dni chorobowego liczy się wg roku choroby, nie roku wypłaty.</summary>
public class PayrollSickCalendarYearTests
{
    [Fact]
    public void Fifty_year_transition_uses_illness_year_not_payment_year()
    {
        var employee = new Employee
        {
            FirstName = "Anna", LastName = "Kowalska", ContractType = ContractType.UmowaOPrace,
            BirthDate = new DateOnly(1975, 6, 1)
        };
        var used = new PayrollYearToDate(EmployerSickDays: 20);
        // 2025: rok ukończenia 50 lat — nadal 33 dni.
        // Wypłata w styczniu 2026 nie może zmienić limitu za grudzień 2025.
        var december = PayrollCalculator.Compute(employee, 6000m, used, PayrollRules.Y2026, 1.67m,
            new PayrollAbsence(10, 0, 20, SickYear: 2025));
        Assert.Equal(10, december.SickDaysEmployer);
        // 2026: od roku następującego po ukończeniu 50 lat — limit 14 dni.
        var january = PayrollCalculator.Compute(employee, 6000m, used, PayrollRules.Y2026, 1.67m,
            new PayrollAbsence(10, 0, 20, SickYear: 2026));
        Assert.Equal(0, january.SickDaysEmployer);
    }

    [Fact]
    public async Task December_sickness_paid_in_january_does_not_consume_january_limit()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(2025, 1, 1),
            BirthDate = new DateOnly(1980, 1, 1)
        });
        // Wcześniej zaksięgowana grudniowa choroba (30 dni) została wypłacona
        // w styczniu. Nie może zmniejszać limitu styczniowego.
        var december = new PayrollList
        {
            CompanyId = books.Company.Id, Month = new DateOnly(2025, 12, 1),
            PaymentDate = new DateOnly(2026, 1, 10),
            Number = "LP/12/2025", Status = PayrollStatus.Zaksiegowana,
            Items = { new PayrollItem { EmployeeId = employee.Id, ContractType = ContractType.UmowaOPrace,
                EmployeeName = employee.FullName, SickDays = 30, SickDaysEmployer = 30 } }
        };
        books.Db.PayrollLists.Add(december);
        await books.Db.SaveChangesAsync();
        var january = await payroll.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await payroll.ChangeAsync(january.Items.Single().Id, null, 10, null);
        Assert.Equal(10, january.Items.Single().SickDaysEmployer);
    }

    [Fact]
    public async Task January_sickness_counts_in_february_even_if_paid_in_march()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(2025, 1, 1),
            BirthDate = new DateOnly(1980, 1, 1)
        });
        // Wcześniejszy miesiąc choroby z późniejszą datą wypłaty.
        // Poprzednia implementacja filtrowała tylko wg PaymentDate i gubiła te dni.
        var january = new PayrollList
        {
            CompanyId = books.Company.Id, Month = new DateOnly(2026, 1, 1),
            PaymentDate = new DateOnly(2026, 3, 10),
            Number = "LP/1/2026", Status = PayrollStatus.Zaksiegowana,
            Items = { new PayrollItem { EmployeeId = employee.Id, ContractType = ContractType.UmowaOPrace,
                EmployeeName = employee.FullName, SickDays = 30, SickDaysEmployer = 30 } }
        };
        books.Db.PayrollLists.Add(january);
        await books.Db.SaveChangesAsync();
        var february = await payroll.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await payroll.ChangeAsync(february.Items.Single().Id, null, 10, null);
        Assert.Equal(3, february.Items.Single().SickDaysEmployer);
    }
}
