using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class PayrollTests
{
    private static readonly PayrollRules R = PayrollRules.Y2026;

    [Fact]
    public void Employment_contract_6000_gross_with_pit2()
    {
        var e = new Employee { FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace, Pit2 = true };
        var i = PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(585.60m, i.PensionEmployee);
        Assert.Equal(90.00m, i.DisabilityEmployee);
        Assert.Equal(147.00m, i.SicknessEmployee);
        Assert.Equal(5177.40m, i.HealthBase);
        Assert.Equal(465.97m, i.Health);
        Assert.Equal(250m, i.CostsOfIncome);
        Assert.Equal(4927m, i.TaxBase);
        Assert.Equal(291m, i.TaxAdvance); // 4927 × 12% − 300
        Assert.Equal(4420.43m, i.Net);
        Assert.Equal(585.60m, i.PensionEmployer);
        Assert.Equal(390.00m, i.DisabilityEmployer);
        Assert.Equal(100.20m, i.Accident);
        Assert.Equal(147.00m, i.LaborFund);
        Assert.Equal(6.00m, i.Fgsp);
    }

    [Fact]
    public void Thresholds_caps_and_special_cases()
    {
        var e = new Employee { FirstName = "A", LastName = "B", ContractType = ContractType.UmowaOPrace };
        // limit 30-krotności: pozostało 1000 zł podstawy
        var capped = PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(PensionBasis: 281600m, TaxBase: 119000m), R, 1.67m);
        Assert.Equal(97.60m, capped.PensionEmployee);
        Assert.Contains("32%", capped.Notes);
        Assert.True(capped.TaxAdvance > 591m);

        var student = new Employee { FirstName = "S", LastName = "T", ContractType = ContractType.UmowaZlecenie, StudentUnder26 = true, YouthRelief = true };
        var s = PayrollCalculator.Compute(student, 3000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(0m, s.SocialEmployee + s.Health + s.TaxAdvance + s.EmployerCost);
        Assert.Equal(3000m, s.Net);
        Assert.Equal(3000m, s.ExemptRevenue);

        var small = new Employee { FirstName = "M", LastName = "K", ContractType = ContractType.UmowaODzielo };
        var f = PayrollCalculator.Compute(small, 200m, new PayrollYearToDate(), R, 1.67m);
        Assert.True(f.FlatRateTax);
        Assert.Equal(24m, f.TaxAdvance);
        Assert.Equal(0m, f.Health);

        var zl = new Employee { FirstName = "Z", LastName = "L", ContractType = ContractType.UmowaZlecenie };
        var z = PayrollCalculator.Compute(zl, 1000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(0m, z.SicknessEmployee);
        Assert.Equal(0m, z.LaborFund); // poniżej minimalnego wynagrodzenia
        Assert.Equal(1.00m, z.Fgsp);
        Assert.Equal(R2((1000m - 112.60m) * 0.2m), z.CostsOfIncome);

        var board = new Employee { FirstName = "P", LastName = "Z", ContractType = ContractType.Powolanie };
        var b = PayrollCalculator.Compute(board, 2000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(0m, b.SocialEmployee);
        Assert.Equal(180m, b.Health);
    }

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public async Task Payroll_list_is_posted_and_declaration_summaries_are_built()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => svc.AddEmployeeAsync(new Employee { FirstName = "X", LastName = "Y", Pesel = "12345678901", StartDate = new DateOnly(2026, 1, 1) }));
        var jan = await svc.AddEmployeeAsync(new Employee { FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace, Gross = 6000m, Pit2 = true, StartDate = new DateOnly(2026, 1, 1), Pesel = "44051401359" });
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ola", LastName = "Zlecenie", ContractType = ContractType.UmowaODzielo, Gross = 150m, StartDate = new DateOnly(2026, 1, 1) });
        await Assert.ThrowsAsync<DomainException>(() => svc.SetAccidentRateAsync(5m));

        var list = await svc.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Assert.Equal(2, list.Items.Count);
        var item = list.Items.Single(i => i.EmployeeId == jan.Id);
        await svc.ChangeGrossAsync(item.Id, 6000m);
        await svc.PostAsync(list.Id);
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(list.Id));
        await Assert.ThrowsAsync<DomainException>(() => svc.DeleteListAsync(list.Id));

        Assert.Equal(6150m, await t.BalanceAsync("404"));
        Assert.Equal(1228.80m, await t.BalanceAsync("405"));
        Assert.Equal(-(4420.43m + 150m - 18m), await t.BalanceAsync("230"));
        Assert.Equal(-(291m + 18m), await t.BalanceAsync("220-02"));
        Assert.Equal(-(822.60m + 465.97m + 1228.80m), await t.BalanceAsync("220-03"));
        var open = await t.Db.OpenItems.AsNoTracking().Where(o => o.AccountNumber == "230").ToListAsync();
        Assert.Equal(2, open.Count);
        Assert.All(open, o => Assert.Equal(OpenItemSide.Zobowiazanie, o.Side));
        Assert.All(open, o => Assert.Equal(new DateOnly(2026, 1, 31), o.DueDate));

        var rs = new ReportService(t.Db, t.User, t.Clock);
        var zus = await rs.ZusMonthlyAsync(new DateOnly(2026, 1, 1));
        Assert.Contains(zus.Rows, r => (string?)r.Cells[0] == "DRA — razem" && (decimal?)r.Cells[3] == 1171.20m);
        var pit4 = await rs.Pit4RAsync(2026);
        Assert.Equal(309m, pit4.Rows.Single(r => (string?)r.Cells[0] == "RAZEM").Cells[5]);
        Assert.Equal(18m, pit4.Rows.Single(r => (string?)r.Cells[0] == "RAZEM").Cells[4]);
        var pit11 = await rs.Pit11Async(2026);
        var row = Assert.Single(pit11.Rows);
        Assert.Equal(6000m, row.Cells[3]);
        Assert.Equal(291m, row.Cells[6]);
        var lp = await rs.PayrollListAsync(list.Id);
        Assert.Contains(lp.Rows, r => r.IsTotal && (decimal?)r.Cells[10] == 4420.43m + 132m);

        // narastająco: lutowa lista widzi styczniową podstawę
        var feb = await svc.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        Assert.Equal(2, feb.Items.Count);
    }
}
