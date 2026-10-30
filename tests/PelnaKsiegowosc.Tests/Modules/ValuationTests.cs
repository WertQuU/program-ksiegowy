using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class ValuationTests
{
    [Fact]
    public async Task Receivable_allowances_follow_aging_and_write_off_uses_them()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Dłużnik");
        await SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, cp.Id, 1000m, new DateOnly(2026, 1, 12)); // termin 26.01, brutto 1230
        var svc = new ValuationService(t.Db, t.User, t.Clock);

        Assert.Empty(await svc.ProposeAsync(new DateOnly(2026, 6, 30))); // 155 dni — poniżej progu
        var p = Assert.Single(await svc.ProposeAsync(new DateOnly(2026, 9, 30)));
        Assert.Equal(247, p.DaysOverdue);
        Assert.Equal(615m, p.Change);
        Assert.Equal(1, await svc.PostProposalsAsync(new DateOnly(2026, 9, 30)));
        Assert.Equal(-615m, await t.BalanceAsync("290"));
        Assert.Equal(615m, await t.BalanceAsync("761-02"));
        Assert.DoesNotContain(await svc.ProposeAsync(new DateOnly(2026, 9, 30)), x => x.Change != 0);
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAllowanceAsync(p.OpenItemId, 700m, new DateOnly(2026, 10, 1), "za dużo"));

        // częściowe rozwiązanie
        await svc.PostAllowanceAsync(p.OpenItemId, -115m, new DateOnly(2026, 10, 31), "częściowa ugoda");
        Assert.Equal(-115m, await t.BalanceAsync("760-04"));
        Assert.Equal(500m, await svc.ExistingAllowanceAsync(p.OpenItemId));

        // odpis NKUP, rozwiązanie — przychód niepodatkowy
        var rec = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 10);
        Assert.Contains(rec.Adjustments, a => a.Item.Contains("761-02") && a.Amount == 615m);
        Assert.Contains(rec.Adjustments, a => a.Item.Contains("760-04") && a.Amount == -115m);

        await svc.WriteOffAsync(p.OpenItemId, new DateOnly(2026, 12, 31), "postanowienie o nieściągalności");
        Assert.Equal(0m, await t.BalanceAsync("200"));
        Assert.Equal(0m, await t.BalanceAsync("290"));
        Assert.Equal(730m, await t.BalanceAsync("761-03"));
        var item = await t.Db.OpenItems.AsNoTracking().SingleAsync(o => o.Id == p.OpenItemId);
        Assert.Equal(OpenItemStatus.Rozliczona, item.Status);
        Assert.Empty(await svc.ProposeAsync(new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public async Task Provisions_are_nkup_until_used_and_holiday_provision_is_computed()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new ValuationService(t.Db, t.User, t.Clock);
        var cit = new CitService(t.Db, t.User, t.Clock);
        var before = (await cit.ReconcileAsync(t.Year.Id, 12)).TaxCosts;

        var audit = await svc.CreateProvisionAsync(ProvisionKind.NaKoszty, "Badanie sprawozdania finansowego", 5000m, new DateOnly(2026, 11, 30));
        Assert.Equal("840", audit.AccountNumber);
        Assert.Equal(-5000m, await t.BalanceAsync("840"));
        Assert.Equal(5000m, await t.BalanceAsync("402"));
        var r1 = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Equal(before, r1.TaxCosts); // utworzenie rezerwy nie zwiększa KUP
        Assert.Contains(r1.Adjustments, a => a.Amount == 5000m);

        await svc.UseProvisionAsync(audit.Id, 3000m, new DateOnly(2026, 12, 15), release: false);
        await svc.UseProvisionAsync(audit.Id, 2000m, new DateOnly(2026, 12, 31), release: true);
        await Assert.ThrowsAsync<DomainException>(() => svc.UseProvisionAsync(audit.Id, 1m, new DateOnly(2026, 12, 31), release: true));
        Assert.Equal(0m, await t.BalanceAsync("840"));
        Assert.Equal(0m, await t.BalanceAsync("402"));
        Assert.Equal(before, (await cit.ReconcileAsync(t.Year.Id, 12)).TaxCosts);
        Assert.Equal(0m, (await svc.ProvisionsAsync()).Single().Remaining);

        var emp = await new PayrollService(t.Db, t.User, t.Clock).AddEmployeeAsync(new Employee
            { FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace, Gross = 6000m, StartDate = new DateOnly(2026, 1, 1) });
        var rows = await svc.HolidayProvisionAsync(new Dictionary<Guid, decimal> { [emp.Id] = 5m }, 2026);
        var coefficient = Enumerable.Range(1, 12).Sum(m => PolishHolidays.WorkingDays(2026, m)) / 12m;
        var row = Assert.Single(rows);
        Assert.Equal(Math.Round(7228.80m / coefficient, 2, MidpointRounding.AwayFromZero), row.DailyRate);
        Assert.Equal(Math.Round(row.DailyRate * 5, 2), row.Amount);
        var holiday = await svc.CreateProvisionAsync(ProvisionKind.Urlopowa, "Niewykorzystane urlopy 31.12.2026", row.Amount, new DateOnly(2026, 12, 31));
        Assert.Equal("843", holiday.AccountNumber);
        Assert.Equal(-row.Amount, await t.BalanceAsync("843"));
    }
}
