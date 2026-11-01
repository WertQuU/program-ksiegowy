using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>
/// Art. 92 KP: kolejność okresów choroby kontrolujemy osobno dla każdej osoby.
/// Inny pracownik z późniejszą chorobą nie może blokować wcześniejszej listy.
/// </summary>
public class PayrollSickChronologyPersonIsolationTests
{
    private static Employee Worker(string firstName) => new()
    {
        FirstName = firstName,
        LastName = "Testowy",
        ContractType = ContractType.UmowaOPrace,
        Gross = 6000m,
        StartDate = new DateOnly(2026, 1, 1),
        BirthDate = new DateOnly(1980, 1, 1)
    };

    private static async Task<PayrollList> CreateFuturePostedSicknessAsync(
        TestBooks books, Employee employee)
    {
        // Izolujemy wyłącznie sprawdzenie chronologii w PostAsync.
        // Rzeczywista późniejsza lista może zawierać inne osoby niż wcześniejsza.
        var future = new PayrollList
        {
            CompanyId = books.Company.Id,
            Month = new DateOnly(2026, 5, 1),
            PaymentDate = new DateOnly(2026, 6, 30),
            Number = "LP/FUTURE/2026",
            Status = PayrollStatus.Zaksiegowana,
            Items =
            {
                new PayrollItem
                {
                    EmployeeId = employee.Id,
                    EmployeeName = employee.FullName,
                    ContractType = ContractType.UmowaOPrace,
                    SickDays = 1,
                    SickDaysEmployer = 1
                }
            }
        };
        books.Db.PayrollLists.Add(future);
        await books.Db.SaveChangesAsync();
        return future;
    }

    [Fact]
    public async Task Later_sickness_of_another_person_does_not_block_earlier_sickness()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var anna = await payroll.AddEmployeeAsync(Worker("Anna"));
        var jan = await payroll.AddEmployeeAsync(Worker("Jan"));

        // Obie osoby są na wcześniejszej liście, ale choruje tylko Jan.
        var april = await payroll.CreateListAsync(
            new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30));
        await payroll.ChangeAsync(april.Items.Single(i => i.EmployeeId == jan.Id).Id,
            null, 1, null);
        await CreateFuturePostedSicknessAsync(books, anna);

        // Brak potwierdzenia art. 92 nadal blokuje księgowanie Jana.
        // Nie może jednak zadziałać fałszywa blokada z powodu choroby Anny.
        var error = await Assert.ThrowsAsync<DomainException>(() => payroll.PostAsync(april.Id));
        Assert.Contains("brak potwierdzenia art. 92 KP", error.Message);
        Assert.DoesNotContain("późniejszej listy", error.Message);
        Assert.Equal(PayrollStatus.Robocza, april.Status);
        Assert.Null(april.JournalEntryId);
    }

    [Fact]
    public async Task Later_sickness_of_same_person_still_blocks_earlier_sickness()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        await payroll.AddEmployeeAsync(Worker("Anna"));
        var jan = await payroll.AddEmployeeAsync(Worker("Jan"));

        var april = await payroll.CreateListAsync(
            new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30));
        await payroll.ChangeAsync(april.Items.Single(i => i.EmployeeId == jan.Id).Id,
            null, 1, null);
        await CreateFuturePostedSicknessAsync(books, jan);

        var error = await Assert.ThrowsAsync<DomainException>(() => payroll.PostAsync(april.Id));
        Assert.Contains("późniejszej listy", error.Message);
        Assert.Equal(PayrollStatus.Robocza, april.Status);
        Assert.Null(april.JournalEntryId);
    }
}
