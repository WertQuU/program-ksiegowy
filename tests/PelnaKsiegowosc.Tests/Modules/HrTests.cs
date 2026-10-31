using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class HrTests
{
    [Fact]
    public void Leave_entitlement_is_proportional_in_first_year()
    {
        var e = new Employee { ContractType = ContractType.UmowaOPrace, StartDate = new DateOnly(2026, 4, 1), AnnualLeaveDays = 26 };
        Assert.Equal(20m, HrService.Entitlement(e, 2026)); // 26 × 9/12 = 19,5 → 20
        Assert.Equal(26m, HrService.Entitlement(e, 2027));
        Assert.Equal(5, HrService.WorkingDays(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15)) + 1); // 11.11 święto → 4 dni robocze
    }

    [Fact]
    public async Task Absences_feed_leave_balance_payroll_and_holiday_provision()
    {
        await using var t = await TestBooks.CreateAsync();
        var payroll = new PayrollService(t.Db, t.User, t.Clock);
        var hr = new HrService(t.Db, t.User, t.Clock);
        var e = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace, Gross = 6000m, Pit2 = true,
            StartDate = new DateOnly(2026, 1, 1), Pesel = "44051401359", AnnualLeaveDays = 26, MedicalExamUntil = new DateOnly(2026, 1, 20)
        });
        await hr.AddAbsenceAsync(e.Id, AbsenceKind.UrlopWypoczynkowy, new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 6)); // 5 dni
        await hr.AddAbsenceAsync(e.Id, AbsenceKind.UrlopNaZadanie, new DateOnly(2026, 2, 9), new DateOnly(2026, 2, 9));
        await Assert.ThrowsAsync<DomainException>(() => hr.AddAbsenceAsync(e.Id, AbsenceKind.Choroba, new DateOnly(2026, 2, 6), new DateOnly(2026, 2, 10)));
        await Assert.ThrowsAsync<DomainException>(() => hr.AddAbsenceAsync(e.Id, AbsenceKind.UrlopNaZadanie, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 6)));
        await hr.AddAbsenceAsync(e.Id, AbsenceKind.Choroba, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 19)); // 10 dni kalendarzowych
        var bal = await hr.LeaveBalanceAsync(e.Id, 2026);
        Assert.Equal(6m, bal.Used);
        Assert.Equal(20m, bal.Remaining);

        // Podstawa chorobowego w marcu: pełne, zaksięgowane miesiące
        // zatrudnienia styczeń i luty (art. 36 ust. 2 ustawy zasiłkowej).
        var january = await payroll.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await payroll.PostAsync(january.Id);
        var february = await payroll.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        await payroll.PostAsync(february.Id);

        var march = await payroll.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await payroll.RecordSickWageAssessmentAsync(e.Id, march.Month, 0, SickWageCause.Ordinary80, "OSW/2026/3");
        await payroll.RecalculateAsync(march.Id);
        var item = Assert.Single(march.Items);
        Assert.Equal(10, item.SickDays);
        Assert.Equal(1380.64m, item.SickPay);

        var rows = await new ValuationService(t.Db, t.User, t.Clock).HolidayProvisionFromLeaveAsync(2026);
        Assert.Equal(20m, Assert.Single(rows).UnusedDays);
        Assert.Equal(26m + 20m, (await hr.LeaveBalanceAsync(e.Id, 2027)).Remaining); // zaległy przechodzi na kolejny rok

        var reminders = await hr.RemindersAsync();
        Assert.Contains(reminders, r => r.What.Contains("badania"));

        await payroll.PostAsync(march.Id);
        var rs = new ReportService(t.Db, t.User, t.Clock);
        var card = await rs.SalaryCardAsync(e.Id, 2026);
        Assert.Contains(card.Rows, r => r.IsTotal && (decimal?)r.Cells[3] == 1380.64m);
        var leave = await rs.LeaveReportAsync(2026);
        Assert.Contains(leave.Rows, r => (decimal?)r.Cells[5] == 20m);
    }
}
