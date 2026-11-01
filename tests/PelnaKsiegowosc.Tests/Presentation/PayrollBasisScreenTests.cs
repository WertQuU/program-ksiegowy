using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels;
using PelnaKsiegowosc.Presentation.ViewModels.Sections;

namespace PelnaKsiegowosc.Tests.Presentation;

public class PayrollBasisScreenTests
{
    [Fact]
    public async Task Screen_requires_separate_evidence_and_explicit_confirmation_before_calculating_sick_basis()
    {
        using var root = new TempRoot();
        var (session, dialogs) = SelfTestScenario.CreateSession(root.Path);
        await SelfTestScenario.RunWizardAsync(session, dialogs);
        await using var db = session.OpenDb();
        var chart = await db.Charts.SingleAsync(c => c.FiscalYearId == session.Year!.Id);
        var chartService = new PelnaKsiegowosc.Application.Ledger.ChartService(db, session.User, session.Clock);
        foreach (var template in PelnaKsiegowosc.Application.Templates.ChartTemplate.Accounts.Where(a => a.Number is "230" or "404" or "405"))
            if (!await db.Accounts.AnyAsync(a => a.ChartId == chart.Id && a.Number == template.Number))
                await chartService.AddAccountAsync(chart.Id, template.Number, template.Name, template.Type,
                    template.BalanceDebitLine, template.BalanceCreditLine, template.ProfitLossLine, template.Settlement, template.Cit);
        var service = new PayrollService(db, session.User, session.Clock);
        var year = session.Year!.StartDate.Year;
        await service.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(year, 1, 1), BirthDate = new DateOnly(1985, 1, 1)
        });
        foreach (var month in new[] { 1, 2 })
        {
            var period = new DateOnly(year, month, 1);
            var history = await service.CreateListAsync(period, period.AddMonths(1).AddDays(-1));
            await service.PostAsync(history.Id);
        }
        var march = await service.CreateListAsync(new DateOnly(year, 3, 1), new DateOnly(year, 3, 31));
        var item = march.Items.Single();
        await service.ChangeAsync(item.Id, null, 10, null);
        await service.RecordSickWageAssessmentAsync(item.EmployeeId, march.Month, 0, SickWageCause.Ordinary80, "OSW/3");
        var vm = new PayrollViewModel(session, dialogs)
        {
            SelectedList = new TableRow(march.Id, []), SelectedItem = new TableRow(item.Id, []),
            SickBasisEvidenceReference = "PODSTAWA/3"
        };
        await vm.ConfirmSickBasisCommand.ExecuteAsync(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("Potwierdź brak"));
        dialogs.Errors.Clear();
        vm.NoOtherSickBasisComponents = true;
        await vm.ConfirmSickBasisCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        await using var check = session.OpenDb();
        Assert.Equal(1380.64m, (await check.PayrollItems.SingleAsync(i => i.Id == item.Id)).SickPay);
        Assert.False(vm.NoOtherSickBasisComponents);
        Assert.Empty(vm.SickBasisEvidenceReference);
    }
}

