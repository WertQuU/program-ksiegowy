using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Art. 36–43 ustawy zasiłkowej: nie wolno księgować szacunkowej podstawy chorobowego.</summary>
public class PayrollSickBasisAuditTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public async Task Missing_previous_payroll_history_blocks_sickness_posting()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = Start
        });
        var list = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(list.Items.Single().Id, null, 10, null);
        Assert.Contains("WSTĘPNA", list.Items.Single().Notes);
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(list.Id));
        Assert.Contains("brak jednoznacznej", ex.Message);
        Assert.Equal(PayrollStatus.Robocza, list.Status);
    }

    [Fact]
    public async Task Complete_stable_history_allows_correct_sick_pay_posting()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = Start
        });
        var jan = await service.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await service.PostAsync(jan.Id);
        var feb = await service.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await service.PostAsync(feb.Id);
        var march = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(march.Items.Single().Id, null, 10, null);
        Assert.Equal(1380.64m, march.Items.Single().SickPay);
        Assert.DoesNotContain("WSTĘPNA", march.Items.Single().Notes ?? "");
        await service.PostAsync(march.Id);
        Assert.Equal(PayrollStatus.Zaksiegowana, march.Status);
    }

    [Fact]
    public async Task Changed_pay_or_possible_change_of_work_time_requires_manual_sick_basis_review()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = Start
        });
        var jan = await service.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await service.PostAsync(jan.Id);
        var feb = await service.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await service.ChangeGrossAsync(feb.Items.Single().Id, 7000m);
        await service.PostAsync(feb.Id);
        var march = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(march.Items.Single().Id, null, 10, null);
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(march.Id));
        Assert.Contains("zmiana wynagrodzenia", ex.Message);
    }
}
