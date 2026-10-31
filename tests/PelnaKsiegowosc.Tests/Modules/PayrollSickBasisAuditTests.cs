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
            Gross = 6000m, StartDate = Start, BirthDate = new DateOnly(1985, 1, 1)
        });
        var list = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(list.Items.Single().Id, null, 10, null);
        await service.RecordSickWageAssessmentAsync(list.Items.Single().EmployeeId, list.Month, 0, SickWageCause.Ordinary80, "OSW/2026/3");
        await service.RecalculateAsync(list.Id);
        Assert.Contains("WSTĘPNA", list.Items.Single().Notes);
        Assert.Equal(0m, list.Items.Single().SickPay); // bez zweryfikowanej podstawy nie wyświetlamy fikcyjnego świadczenia
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
            Gross = 6000m, StartDate = Start, BirthDate = new DateOnly(1985, 1, 1)
        });
        var jan = await service.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await service.PostAsync(jan.Id);
        var feb = await service.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await service.PostAsync(feb.Id);
        var march = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(march.Items.Single().Id, null, 10, null);
        await service.RecordSickWageAssessmentAsync(march.Items.Single().EmployeeId, march.Month, 0, SickWageCause.Ordinary80, "OSW/2026/3");
        await service.RecalculateAsync(march.Id);
        Assert.Equal(1380.64m, march.Items.Single().SickPay);
        Assert.DoesNotContain("WSTĘPNA", march.Items.Single().Notes ?? "");
        await service.PostAsync(march.Id);
        Assert.Equal(PayrollStatus.Zaksiegowana, march.Status);
    }

    [Fact]
    public async Task Unknown_age_blocks_sickness_posting_even_with_complete_pay_history()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Bez daty", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = Start
        });
        var jan = await service.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await service.PostAsync(jan.Id);
        var feb = await service.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await service.PostAsync(feb.Id);
        var march = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(march.Items.Single().Id, null, 5, null);
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(march.Id));
        Assert.Contains("daty urodzenia", ex.Message);
    }

    [Fact]
    public async Task Changed_pay_or_possible_change_of_work_time_requires_manual_sick_basis_review()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = Start, BirthDate = new DateOnly(1985, 1, 1)
        });
        var jan = await service.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await service.PostAsync(jan.Id);
        var feb = await service.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await service.ChangeGrossAsync(feb.Items.Single().Id, 7000m);
        await service.PostAsync(feb.Id);
        var march = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(march.Items.Single().Id, null, 10, null);
        await service.RecordSickWageAssessmentAsync(march.Items.Single().EmployeeId, march.Month, 0, SickWageCause.Ordinary80, "OSW/2026/3");
        await service.RecalculateAsync(march.Id);
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(march.Id));
        Assert.Contains("zmiana wynagrodzenia", ex.Message);
    }

    [Fact]
    public async Task Verified_sick_basis_without_art92_attestation_is_not_postable()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Ala", LastName = "BezOswiadczenia", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = Start, BirthDate = new DateOnly(1980, 1, 1)
        });
        var jan = await service.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await service.PostAsync(jan.Id);
        var feb = await service.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await service.PostAsync(feb.Id);
        var march = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(march.Items.Single().Id, null, 10, null);
        Assert.Equal(0m, march.Items.Single().SickPay);
        Assert.Contains("WSTĘPNA", march.Items.Single().Notes);
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(march.Id));
        Assert.Contains("potwierdzenia art. 92 KP", ex.Message);
        Assert.Equal(PayrollStatus.Robocza, march.Status);
    }

    [Fact]
    public async Task Art92_assessment_applies_100_percent_and_previous_employer_days()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new PayrollService(books.Db, books.User, books.Clock);
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Ala", LastName = "Zaswiadczenie", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = Start, BirthDate = new DateOnly(1980, 1, 1)
        });
        var jan = await service.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await service.PostAsync(jan.Id);
        var feb = await service.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await service.PostAsync(feb.Id);
        var march = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.ChangeAsync(march.Items.Single().Id, null, 10, null);
        await service.RecordSickWageAssessmentAsync(march.Items.Single().EmployeeId, march.Month,
            30, SickWageCause.Pregnancy100, "OSW/2026/3");
        await service.RecalculateAsync(march.Id);
        Assert.Equal(3, march.Items.Single().SickDaysEmployer);
        Assert.Equal(517.74m, march.Items.Single().SickPay); // 6000 × 86,29% / 30 × 3
        Assert.Contains("100%", march.Items.Single().Notes);
        // 3 z 10 dni opłaca pracodawca, pozostałe 7 to odrębny zasiłek.
        // Nie zatwierdzamy niekompletnej listy: byłaby myląca podatkowo i płacowo.
        var blocked = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(march.Id));
        Assert.Contains("zasiłku chorobowego", blocked.Message);
        Assert.Equal(PayrollStatus.Robocza, march.Status);
        // Odrzucona lista nie tworzy zapisu księgowego, można ją jeszcze skorygować.
        Assert.Null(march.JournalEntryId);
    }
}
