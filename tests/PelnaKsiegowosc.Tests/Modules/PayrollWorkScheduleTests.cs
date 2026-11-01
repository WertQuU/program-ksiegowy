using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class PayrollWorkScheduleTests
{
    internal static async Task<(PayrollService Service, PayrollList List, Employee Employee, WorkScheduleDay[] Days)> PrepareAsync(
        TestBooks books, WorkScheduleDayKind remaining = WorkScheduleDayKind.Praca)
    {
        var service = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(2026, 1, 1), BirthDate = new DateOnly(1980, 1, 1)
        });
        foreach (var month in new[] { 1, 2 })
        {
            var start = new DateOnly(2026, month, 1);
            var history = await service.CreateListAsync(start, start.AddMonths(1).AddDays(-1));
            await service.PostAsync(history.Id);
        }
        var hr = new HrService(books.Db, books.User, books.Clock);
        var remainingDate = new DateOnly(2026, 3, remaining == WorkScheduleDayKind.Wolne ? 1 : 31);
        await hr.AddAbsenceAsync(employee.Id, AbsenceKind.Choroba,
            new DateOnly(2026, 3, remainingDate.Day == 1 ? 2 : 1), new DateOnly(2026, 3, remainingDate.Day == 1 ? 31 : 30));
        if (remaining == WorkScheduleDayKind.NieodplatnaNieobecnosc)
            await hr.AddAbsenceAsync(employee.Id, AbsenceKind.UrlopBezplatny, remainingDate, remainingDate);
        var list = await service.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await service.RecordSickWageAssessmentAsync(employee.Id, list.Month, 0, SickWageCause.Ordinary80, "OSW/3");
        await service.ConfirmSickBasisCompletenessAsync(list.Id, employee.Id, true, "PODSTAWA/3");
        var days = Enumerable.Range(1, 31).Select(n =>
        {
            var date = new DateOnly(2026, 3, n);
            var scheduled = HrService.WorkingDays(date, date) == 1 ? 8m : 0m;
            return new WorkScheduleDay(date, scheduled, date == remainingDate && remaining == WorkScheduleDayKind.Praca ? 8m : 0m,
                date == remainingDate ? remaining : WorkScheduleDayKind.Choroba);
        }).ToArray();
        return (service, list, employee, days);
    }

    [Theory]
    [InlineData(WorkScheduleDayKind.Praca, 272.73)]
    [InlineData(WorkScheduleDayKind.Wolne, 0)]
    [InlineData(WorkScheduleDayKind.NieodplatnaNieobecnosc, 0)]
    public async Task Documented_schedule_calculates_work_pay_and_allows_posting(WorkScheduleDayKind kind, double expectedWorkPay)
    {
        await using var books = await TestBooks.CreateAsync();
        var (service, list, employee, days) = await PrepareAsync(books, kind);
        var item = list.Items.Single();
        await service.ConfirmWorkScheduleAsync(list.Id, item.Id, days, "GRAFIK/2026/03 + EWIDENCJA/03");
        await service.RecalculateAsync(list.Id);
        Assert.Equal((decimal)expectedWorkPay, item.SocialBasis); // 6000 / 176 * 8 = 272.73, otherwise no work
        Assert.Equal(4141.92m, item.SickPay); // 6000 * (1 - 13.71%) * 80% / 30 * 30
        Assert.DoesNotContain("WSTĘPNA", item.Notes);
        if (kind == WorkScheduleDayKind.Praca)
        {
            Assert.Equal(4414.65m, item.Gross);
            Assert.Equal(26.62m, item.PensionEmployee);
            Assert.Equal(4.09m, item.DisabilityEmployee);
            Assert.Equal(6.68m, item.SicknessEmployee);
            Assert.Equal(393.95m, item.Health);
        }
        await service.PostAsync(list.Id);
        Assert.Equal(PayrollStatus.Zaksiegowana, list.Status);
        Assert.True(await books.Db.AuditEvents.AnyAsync(a => a.Action == "GRAFIK_30_31_POTWIERDZONY"));
        await Assert.ThrowsAsync<DomainException>(() => service.ConfirmWorkScheduleAsync(list.Id, item.Id, days, "zmiana"));
    }

    [Fact]
    public async Task Changed_inputs_invalidate_schedule_and_keep_posting_blocked()
    {
        await using var books = await TestBooks.CreateAsync();
        var (service, list, _, days) = await PrepareAsync(books);
        var item = list.Items.Single();
        await service.ConfirmWorkScheduleAsync(list.Id, item.Id, days, "GRAFIK/03");
        await service.RecalculateAsync(list.Id);
        await service.ChangeAsync(item.Id, 6500m, null, null);
        Assert.Contains("WSTĘPNA", item.Notes);
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(list.Id));
        Assert.Contains("grafiku", ex.Message);
        Assert.Equal(PayrollStatus.Robocza, list.Status);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("wrong-date")]
    [InlineData("work-on-sick")]
    [InlineData("negative")]
    [InlineData("over-24")]
    [InlineData("no-reference")]
    public async Task Invalid_or_undocumented_schedule_is_rejected(string problem)
    {
        await using var books = await TestBooks.CreateAsync();
        var (service, list, _, valid) = await PrepareAsync(books);
        var days = valid.ToArray();
        switch (problem)
        {
            case "duplicate": days[0] = days[1]; break;
            case "missing": days = days[..30]; break;
            case "wrong-date": days[0] = days[0] with { Date = new DateOnly(2026, 4, 1) }; break;
            case "work-on-sick": days[1] = days[1] with { WorkedHours = 8, Kind = WorkScheduleDayKind.Praca }; break;
            case "negative": days[1] = days[1] with { ScheduledHours = -1 }; break;
            case "over-24": days[1] = days[1] with { ScheduledHours = 25 }; break;
        }
        await Assert.ThrowsAsync<DomainException>(() => service.ConfirmWorkScheduleAsync(list.Id, list.Items.Single().Id,
            days, problem == "no-reference" ? "" : "GRAFIK/03"));
        await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(list.Id));
    }

    [Fact]
    public async Task Removing_source_absence_invalidates_previously_confirmed_hours()
    {
        await using var books = await TestBooks.CreateAsync();
        var (service, list, employee, days) = await PrepareAsync(books);
        await service.ConfirmWorkScheduleAsync(list.Id, list.Items.Single().Id, days, "GRAFIK/03");
        var absence = await books.Db.Absences.SingleAsync(a => a.EmployeeId == employee.Id);
        await new HrService(books.Db, books.User, books.Clock).DeleteAbsenceAsync(absence.Id);
        await service.RecalculateAsync(list.Id);
        Assert.Contains("WSTĘPNA", list.Items.Single().Notes);
        await Assert.ThrowsAsync<DomainException>(() => service.PostAsync(list.Id));
    }
}
