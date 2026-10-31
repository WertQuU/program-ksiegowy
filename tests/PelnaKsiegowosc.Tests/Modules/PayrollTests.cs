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
    public void Labor_fund_threshold_is_full_minimum_wage_regardless_of_working_time_and_counts_other_titles()
    {
        // 2026: minimalne wynagrodzenie 4806 zł — próg FP nie jest proporcjonalny do etatu
        foreach (var (fraction, gross, expectedFp) in new[] { (0.25m, 1201.50m, 0m), (0.5m, 2403m, 0m), (0.75m, 3604.50m, 0m), (0.5m, 4806m, 117.75m), (1m, 4806m, 117.75m) })
        {
            var e = new Employee { FirstName = "P", LastName = "E", ContractType = ContractType.UmowaOPrace, WorkTimeFraction = fraction };
            var i = PayrollCalculator.Compute(e, gross, new PayrollYearToDate(), R, 1.67m);
            Assert.Equal(expectedFp, i.LaborFund);
            Assert.Equal(R2(gross * 0.001m), i.Fgsp);                                 // FGŚP bez progu
        }
        // 1/2 etatu 2403 zł + umowa zlecenia u tego samego płatnika 2403 zł → łącznie 4806 zł — FP od obu tytułów
        var half = new Employee { FirstName = "P", LastName = "E", ContractType = ContractType.UmowaOPrace, WorkTimeFraction = 0.5m };
        Assert.Equal(58.87m, PayrollCalculator.Compute(half, 2403m, new PayrollYearToDate(), R, 1.67m, otherTitlesFpBasis: 2403m).LaborFund);
        // choroba w miesiącu: pełny etat 4806 zł, po potrąceniu za 10 dni podstawa niższa — porównuje się wynagrodzenie za pełny miesiąc
        var full = new Employee { FirstName = "P", LastName = "E", ContractType = ContractType.UmowaOPrace };
        var sick = PayrollCalculator.Compute(full, 4806m, new PayrollYearToDate(), R, 1.67m, new PayrollAbsence(10, 0, 21));
        Assert.True(sick.SocialBasis < 4806m);
        Assert.Equal(R2(sick.SocialBasis * 0.0245m), sick.LaborFund);
    }

    [Fact]
    public async Task Payroll_list_sums_titles_of_the_same_person_for_labor_fund()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        var start = new DateOnly(2026, 1, 1);
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ola", LastName = "Kowal", Pesel = "90010112349", ContractType = ContractType.UmowaOPrace, WorkTimeFraction = 0.5m, Gross = 2403m, StartDate = start });
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ola", LastName = "Kowal", Pesel = "90010112349", ContractType = ContractType.UmowaZlecenie, Gross = 2403m, StartDate = start });
        var list = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 10));
        Assert.All(list.Items, i => Assert.Equal(58.87m, i.LaborFund));
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

    [Fact]
    public void Ppk_and_absences()
    {
        var e = new Employee { FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace, Pit2 = true, Ppk = true };
        var p = PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(120m, p.PpkEmployee);
        Assert.Equal(90m, p.PpkEmployer);
        Assert.Equal(5017m, p.TaxBase); // przychód obejmuje wpłatę PPK pracodawcy
        Assert.Equal(302m, p.TaxAdvance);
        Assert.Equal(465.97m, p.Health); // wpłata pracodawcy nie zwiększa podstawy zdrowotnej
        Assert.Equal(4289.43m, p.Net);

        var sick = PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(), R, 1.67m, new PayrollAbsence(10, 0, 21));
        Assert.Equal(4000m, sick.SocialBasis);
        Assert.Equal(1380.64m, sick.SickPay); // 6000 × 86,29% × 80% / 30 × 10
        Assert.Equal(5380.64m, sick.Gross);
        Assert.Equal(390.40m, sick.PensionEmployee); // składki tylko od wynagrodzenia za czas pracy
        var unpaid = PayrollCalculator.Compute(new Employee { FirstName = "A", LastName = "B", ContractType = ContractType.UmowaOPrace }, 4200m, new PayrollYearToDate(), R, 1.67m, new PayrollAbsence(0, 2, 21));
        Assert.Equal(3800m, unpaid.Gross);
        Assert.Equal(20, PelnaKsiegowosc.Domain.Cit.PolishHolidays.WorkingDays(2026, 11)); // 1 i 11 listopada
    }

    [Fact]
    public void Employer_sick_pay_is_limited_to_33_or_14_days_and_rules_exist_only_for_verified_years()
    {
        var young = new Employee { FirstName = "A", LastName = "B", ContractType = ContractType.UmowaOPrace, BirthDate = new DateOnly(1990, 1, 1) };
        var old = new Employee { FirstName = "C", LastName = "D", ContractType = ContractType.UmowaOPrace, BirthDate = new DateOnly(1970, 1, 1) }; // 56 lat → 14 dni
        var y = PayrollCalculator.Compute(young, 6000m, new PayrollYearToDate(EmployerSickDays: 30), R, 1.67m, new PayrollAbsence(10, 0, 21));
        Assert.Equal(3, y.SickDaysEmployer);
        Assert.Equal(Math.Round(6000m * 0.8629m * 0.8m / 30m * 3, 2), y.SickPay);
        Assert.Contains("ZASIŁEK CHOROBOWY za 7 dni", y.Notes);
        var o = PayrollCalculator.Compute(old, 6000m, new PayrollYearToDate(EmployerSickDays: 10), R, 1.67m, new PayrollAbsence(10, 0, 21));
        Assert.Equal(4, o.SickDaysEmployer);
        Assert.Equal(14, PayrollRules.EmployerSickDaysLimit(new DateOnly(1970, 6, 1), 2026));
        Assert.Equal(33, PayrollRules.EmployerSickDaysLimit(new DateOnly(1976, 6, 1), 2026)); // 50 lat w 2026 — limit 14 od 2027
        Assert.Equal(4666m, PayrollRules.For(2025).MinimumWage);
        Assert.Throws<DomainException>(() => PayrollRules.For(2027));
    }

    [Fact]
    public void Birth_date_is_read_from_pesel()
    {
        Assert.Equal(new DateOnly(1944, 5, 14), PelnaKsiegowosc.Domain.Validation.Identifiers.BirthDateFromPesel("44051401359"));
        Assert.Equal(new DateOnly(2002, 7, 8), PelnaKsiegowosc.Domain.Validation.Identifiers.BirthDateFromPesel("02270803624"));
        Assert.Null(PelnaKsiegowosc.Domain.Validation.Identifiers.BirthDateFromPesel("12345678901"));
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
