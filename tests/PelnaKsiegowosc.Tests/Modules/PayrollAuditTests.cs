using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Audyt 9cf971a: PIT-2, wspólny limit kosztów autorskich, zbieg tytułów ZUS, PPK, aktualność listy, nieobecności, brutto 0.</summary>
public class PayrollAuditTests
{
    private static readonly PayrollRules R = PayrollRules.Y2026;
    private const string Pesel = "90010112349";
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public void Youth_relief_applies_through_26th_birthday_but_not_the_day_after()
    {
        var employee = new Employee
        {
            FirstName = "Ala", LastName = "Test",
            ContractType = ContractType.UmowaOPrace,
            BirthDate = new DateOnly(2000, 10, 31),
            YouthRelief = true
        };
        var onBirthday = PayrollCalculator.Compute(employee, 6000m, new PayrollYearToDate(), R, 1.67m,
            paymentDate: new DateOnly(2026, 10, 31));
        var dayAfter = PayrollCalculator.Compute(employee, 6000m, new PayrollYearToDate(), R, 1.67m,
            paymentDate: new DateOnly(2026, 11, 1));
        Assert.True(onBirthday.ExemptRevenue > 0m);
        Assert.Equal(0m, dayAfter.ExemptRevenue);
        Assert.True(dayAfter.TaxAdvance > onBirthday.TaxAdvance);
        Assert.Contains("Ulga dla młodych wyłączona", dayAfter.Notes);
    }

    // ---------- PK-01: łączny limit 120 000 zł (art. 22 ust. 9aa) ----------
    [Fact]
    public void Author_costs_and_youth_exempt_revenue_share_120000_limit_not_85528()
    {
        var young = new Employee { FirstName = "M", LastName = "A", ContractType = ContractType.UmowaZlecenie, StudentUnder26 = true, YouthRelief = true, ContractTotalAmount = 200000m,
            Costs = CostsOfIncome.Autorskie50, AuthorCostsBasis = "Umowa z przeniesieniem praw" };
        // błąd: po wyczerpaniu ulgi (85 528 zł) koszty 50% były zerowane — poprawnie pozostaje 120 000 − 85 528 = 34 472 zł
        var afterRelief = PayrollCalculator.Compute(young, 50000m, new PayrollYearToDate(YouthExemptRevenue: 85528m), R, 1.67m);
        Assert.Equal(25000m, afterRelief.AuthorCosts);
        // limit łączny: wykorzystane 30 000 kosztów + 85 528 zwolnienia → pozostało 4 472 zł
        var nearLimit = PayrollCalculator.Compute(young, 50000m, new PayrollYearToDate(YouthExemptRevenue: 85528m, AuthorCosts: 30000m), R, 1.67m);
        Assert.Equal(4472m, nearLimit.AuthorCosts);
        Assert.Contains("limit kosztów 50%", nearLimit.Notes);
        // brzeg: limit wyczerpany dokładnie — 0 kosztów 50%, reszta kosztami zwykłymi (20%)
        var exhausted = PayrollCalculator.Compute(young, 10000m, new PayrollYearToDate(YouthExemptRevenue: 85528m, AuthorCosts: 34472m), R, 1.67m);
        Assert.Equal(0m, exhausted.AuthorCosts);
        Assert.Equal(2000m, exhausted.CostsOfIncome);
        // zwolnienie i koszty w tej samej wypłacie: 100 000 zł — 85 528 zwolnione, koszty 50% od 14 472 zł
        var same = PayrollCalculator.Compute(young, 100000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(85528m, same.ExemptRevenue);
        Assert.Equal(7236m, same.AuthorCosts);
    }

    // ---------- PK-02: PIT-2 raz w miesiącu na osobę, warianty 1/12, 1/24, 1/36 ----------
    [Fact]
    public void Pit2_variants_100_150_300_are_applied()
    {
        foreach (var (reduction, advance) in new[] { (300m, 291m), (150m, 441m), (100m, 491m), (0m, 591m) })
        {
            var e = new Employee { FirstName = "J", LastName = "N", ContractType = ContractType.UmowaOPrace, Pit2Reduction = reduction };
            var i = PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(), R, 1.67m);
            Assert.Equal(reduction, i.TaxReduction);
            Assert.Equal(advance, i.TaxAdvance);                                              // 4927 × 12% = 591,24 − kwota zmniejszająca
        }
        Assert.True(new Employee { Pit2 = true }.Pit2Reduction == 300m);                     // zgodność wsteczna
        Assert.Equal(150m, new Employee { Pit2Reduction = 150m, Pit2 = true }.Pit2Reduction);
    }

    [Fact]
    public async Task Pit2_reduction_is_used_once_per_month_across_contracts_and_lists()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ewa", LastName = "Dwie", Pesel = Pesel, ContractType = ContractType.UmowaOPrace, Gross = 6000m, Pit2Reduction = 300m, StartDate = Start });
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ewa", LastName = "Dwie", Pesel = Pesel, ContractType = ContractType.UmowaZlecenie, Gross = 3000m, ContractTotalAmount = 3000m,
            Pit2Reduction = 300m, ExternalTitles = ExternalInsuranceTitle.Brak, StartDate = Start });
        // dwie umowy tej osoby na jednej liście — łącznie 300 zł, nie 600 zł
        var march = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        Assert.Equal(300m, march.Items.Sum(i => i.TaxReduction));
        Assert.Equal(300m, march.Items.Single(i => i.ContractType == ContractType.UmowaOPrace).TaxReduction);
        await svc.PostAsync(march.Id);
        // druga lista z wypłatą w tym samym miesiącu (np. premia) — kwota zmniejszająca już wykorzystana
        var bonus = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 25), "Premia");
        Assert.Equal(0m, bonus.Items.Sum(i => i.TaxReduction));
        Assert.Contains(bonus.Items, i => (i.Notes ?? "").Contains("wykorzystana już w tym miesiącu"));
        // następny miesiąc wypłaty — ponownie 300 zł
        var april = await svc.CreateListAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 10));
        Assert.Equal(300m, april.Items.Sum(i => i.TaxReduction));
    }

    [Fact]
    public async Task Two_draft_lists_in_the_same_month_cannot_both_post_the_reduction()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        await svc.AddEmployeeAsync(new Employee { FirstName = "Jan", LastName = "N", ContractType = ContractType.UmowaOPrace, Gross = 6000m, Pit2Reduction = 300m, StartDate = Start });
        var a = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        var b = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 20), "Druga");
        Assert.Equal(300m, b.Items.Single().TaxReduction);                                     // obie robocze — każda „widzi” pełną kwotę
        await svc.PostAsync(a.Id);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(b.Id));      // nieaktualna po zaksięgowaniu pierwszej
        Assert.Contains("nieaktualna", ex.Message);
        await svc.RecalculateAsync(b.Id);
        Assert.Equal(0m, b.Items.Single().TaxReduction);
        await svc.PostAsync(b.Id);
    }

    // ---------- PK-03: zbieg tytułów do ubezpieczeń ----------
    [Fact]
    public void Civil_contract_insurance_depends_on_other_titles()
    {
        Employee Zl(ExternalInsuranceTitle titles, decimal? basis = null, bool voluntary = false) => new()
        {
            FirstName = "Z", LastName = "L", ContractType = ContractType.UmowaZlecenie, ContractTotalAmount = 3000m, ExternalTitles = titles, ExternalTitlesBasis = basis,
            VoluntaryPension = voluntary, Ppk = true
        };
        // etat u innego pracodawcy z co najmniej minimalnym wynagrodzeniem — ze zlecenia tylko zdrowotna
        var onlyHealth = PayrollCalculator.Compute(Zl(ExternalInsuranceTitle.EtatUInnegoPracodawcy, 5000m), 3000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(0m, onlyHealth.PensionEmployee + onlyHealth.DisabilityEmployee + onlyHealth.SicknessEmployee);
        Assert.Equal(0m, onlyHealth.PensionEmployer + onlyHealth.Accident + onlyHealth.LaborFund + onlyHealth.Fgsp);
        Assert.Equal(270m, onlyHealth.Health);
        Assert.Equal(0m, onlyHealth.PpkEmployee);                                              // PPK — tylko obowiązkowo ubezpieczeni
        Assert.False(onlyHealth.InsuranceUndetermined);
        // ten sam zbieg + wniosek o dobrowolne emerytalne i rentowe — wypadkowa obowiązkowo, bez FP i FGŚP
        var voluntary = PayrollCalculator.Compute(Zl(ExternalInsuranceTitle.EtatUInnegoPracodawcy, 5000m, true), 3000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(292.80m, voluntary.PensionEmployee);
        Assert.Equal(50.10m, voluntary.Accident);
        Assert.Equal(0m, voluntary.LaborFund + voluntary.Fgsp);
        // etat u innego pracodawcy poniżej minimalnego — zlecenie obowiązkowo (FGŚP tak; FP — podstawa poniżej minimalnego)
        var mandatory = PayrollCalculator.Compute(Zl(ExternalInsuranceTitle.EtatUInnegoPracodawcy, 3000m), 3000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(292.80m, mandatory.PensionEmployee);
        Assert.Equal(3.00m, mandatory.Fgsp);
        // nieustalony zbieg albo brak podstawy z innego tytułu — oznaczenie, lista nie zostanie zaksięgowana
        Assert.True(PayrollCalculator.Compute(Zl(ExternalInsuranceTitle.Nieustalone), 3000m, new PayrollYearToDate(), R, 1.67m).InsuranceUndetermined);
        Assert.True(PayrollCalculator.Compute(Zl(ExternalInsuranceTitle.EtatUInnegoPracodawcy), 3000m, new PayrollYearToDate(), R, 1.67m).InsuranceUndetermined);
        // kolejne zlecenia (art. 9 ust. 2a–2b): obowiązkowe, dopóki łączna podstawa wcześniejszych < minimalne wynagrodzenie
        var brak = Zl(ExternalInsuranceTitle.Brak);
        Assert.True(PayrollCalculator.CivilMandatory(brak, null, R));
        Assert.False(PayrollCalculator.CivilMandatory(brak, 4806m, R));
        Assert.True(PayrollCalculator.CivilMandatory(brak, 4805.99m, R));
        Assert.False(PayrollCalculator.CivilMandatory(Zl(ExternalInsuranceTitle.ZlecenieUInnegoZleceniodawcy, 4806m), null, R));
        Assert.True(PayrollCalculator.CivilMandatory(Zl(ExternalInsuranceTitle.ObowiazkoweWgUstalenia), 99999m, R));
        Assert.False(PayrollCalculator.CivilMandatory(Zl(ExternalInsuranceTitle.DobrowolneWgUstalenia), null, R));
    }

    [Fact]
    public async Task Several_civil_contracts_at_payer_and_undetermined_titles_block_posting()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ola", LastName = "Z", Pesel = Pesel, ContractType = ContractType.UmowaZlecenie, Gross = 5000m, ContractTotalAmount = 5000m,
            ExternalTitles = ExternalInsuranceTitle.Brak, StartDate = Start });
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ola", LastName = "Z", Pesel = Pesel, ContractType = ContractType.UmowaZlecenie, Gross = 1000m, ContractTotalAmount = 1000m,
            ExternalTitles = ExternalInsuranceTitle.Brak, StartDate = new DateOnly(2026, 2, 1) });
        var other = await svc.AddEmployeeAsync(new Employee { FirstName = "Piotr", LastName = "N", ContractType = ContractType.UmowaZlecenie, Gross = 2000m, ContractTotalAmount = 2000m, StartDate = Start });
        var list = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        var first = list.Items.Single(i => i.EmployeeName == "Ola Z" && i.BaseGross == 5000m);
        var second = list.Items.Single(i => i.EmployeeName == "Ola Z" && i.BaseGross == 1000m);
        Assert.Equal(488m, first.PensionEmployee);                                              // pierwsza umowa (wcześniej zawarta) — obowiązkowo
        Assert.Equal(0m, second.PensionEmployee);                                               // podstawa pierwszej ≥ minimalne — druga tylko zdrowotna
        Assert.Equal(90m, second.Health);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(list.Id));     // Piotr — zbieg nieustalony
        Assert.Contains("zbiegu tytułów", ex.Message);
        var p = await t.Db.Employees.AsNoTracking().FirstAsync(e => e.Id == other.Id);
        p.ExternalTitles = ExternalInsuranceTitle.Brak;
        await svc.UpdateEmployeeAsync(p);
        await svc.RecalculateAsync(list.Id);
        await svc.PostAsync(list.Id);
    }

    // ---------- PK-04: PPK niezależnie od ryczałtu; przychód w dniu przekazania wpłaty ----------
    [Fact]
    public void Ppk_is_computed_for_flat_rate_contract_and_employer_part_is_taxed_only_when_transferred()
    {
        var small = new Employee { FirstName = "Z", LastName = "R", ContractType = ContractType.UmowaZlecenie, ContractTotalAmount = 150m, ExternalTitles = ExternalInsuranceTitle.Brak, Ppk = true };
        var flat = PayrollCalculator.Compute(small, 150m, new PayrollYearToDate(), R, 1.67m);
        Assert.True(flat.FlatRateTax);
        Assert.Equal(3.00m, flat.PpkEmployee);
        Assert.Equal(2.25m, flat.PpkEmployer);
        var e = new Employee { FirstName = "Jan", LastName = "N", ContractType = ContractType.UmowaOPrace, Pit2Reduction = 300m, Ppk = true };
        var month = PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(), R, 1.67m);
        Assert.Equal(90m, month.PpkEmployer);
        Assert.Equal(4927m, month.TaxBase);                                                     // wpłata naliczona, jeszcze nieprzekazana — nie jest przychodem
        var afterTransfer = PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(), R, 1.67m, ppkEmployerTaxable: 90m);
        Assert.Equal(5017m, afterTransfer.TaxBase);
        Assert.Equal(302m, afterTransfer.TaxAdvance);
        Assert.Equal(90m, afterTransfer.PpkEmployerTaxed);
        Assert.Throws<DomainException>(() => PayrollCalculator.Compute(e, 6000m, new PayrollYearToDate(), R, 1.67m, ppkEmployerTaxable: -1m));
    }

    [Fact]
    public async Task Employer_ppk_is_taxed_once_in_the_list_paid_after_transfer()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        await svc.AddEmployeeAsync(new Employee { FirstName = "Jan", LastName = "N", ContractType = ContractType.UmowaOPrace, Gross = 6000m, Pit2Reduction = 300m, Ppk = true, StartDate = Start });
        var jan = await svc.CreateListAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Assert.Equal(0m, jan.Items.Single().PpkEmployerTaxed);
        await Assert.ThrowsAsync<DomainException>(() => svc.RecordPpkTransferAsync(jan.Id, new DateOnly(2026, 2, 10)));   // lista robocza
        await svc.PostAsync(jan.Id);
        // lista lutowa utworzona przed zarejestrowaniem przekazania — po rejestracji staje się nieaktualna
        var feb = await svc.CreateListAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 27));
        Assert.Equal(0m, feb.Items.Single().PpkEmployerTaxed);
        await svc.RecordPpkTransferAsync(jan.Id, new DateOnly(2026, 2, 10));
        await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(feb.Id));
        await svc.RecalculateAsync(feb.Id);
        Assert.Equal(90m, feb.Items.Single().PpkEmployerTaxed);
        Assert.Equal(5017m, feb.Items.Single().TaxBase);
        await svc.PostAsync(feb.Id);
        Assert.Equal(feb.Id, (await t.Db.PayrollItems.AsNoTracking().SingleAsync(i => i.PayrollListId == jan.Id)).PpkTaxedInListId);
        await Assert.ThrowsAsync<DomainException>(() => svc.RecordPpkTransferAsync(jan.Id, new DateOnly(2026, 2, 12)));  // już opodatkowana
        // marzec — wpłata styczniowa nie jest opodatkowana ponownie (lutowa nieprzekazana)
        var mar = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        Assert.Equal(0m, mar.Items.Single().PpkEmployerTaxed);
        // PIT-11: przychód obejmuje wyłącznie opodatkowaną (przekazaną) wpłatę
        var pit11 = await new Application.Reports.ReportService(t.Db, t.User, t.Clock).Pit11DataAsync(2026);
        Assert.Equal(12090m, pit11.Single().Sources.Single().Revenue);
    }

    // ---------- PK-05: lista nieaktualna po zmianie danych umowy ----------
    [Fact]
    public async Task Changing_contract_amount_after_calculation_blocks_posting_until_recalculated()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        var e = await svc.AddEmployeeAsync(new Employee { FirstName = "Z", LastName = "R", ContractType = ContractType.UmowaZlecenie, Gross = 150m, ContractTotalAmount = 150m,
            ExternalTitles = ExternalInsuranceTitle.Brak, StartDate = Start });
        var list = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        Assert.True(list.Items.Single().FlatRateTax);
        var changed = await t.Db.Employees.AsNoTracking().FirstAsync(x => x.Id == e.Id);
        changed.ContractTotalAmount = 600m;                                                     // aneks: kwota z umowy 600 zł — ryczałt nie przysługuje
        await svc.UpdateEmployeeAsync(changed);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.PostAsync(list.Id));
        Assert.Contains("Przelicz listę", ex.Message);
        await svc.RecalculateAsync(list.Id);
        Assert.False(list.Items.Single().FlatRateTax);
        await svc.PostAsync(list.Id);
        Assert.Equal(PayrollStatus.Zaksiegowana, list.Status);
    }

    // ---------- PK-06: zmiana kwoty nie kasuje nieobecności ----------
    [Fact]
    public async Task Changing_gross_keeps_absence_days()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        await svc.AddEmployeeAsync(new Employee { FirstName = "Jan", LastName = "N", ContractType = ContractType.UmowaOPrace, Gross = 6000m, StartDate = Start });
        var list = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        var item = list.Items.Single();
        await svc.ChangeAsync(item.Id, null, 10, 2);
        Assert.Equal(10, item.SickDays);
        var sickPay = item.SickPay;
        await svc.ChangeGrossAsync(item.Id, 7000m);
        Assert.Equal(10, item.SickDays);                                                        // błąd: dni choroby były zerowane
        Assert.Equal(2, item.UnpaidDays);
        Assert.True(item.SickPay > sickPay);
        Assert.Equal(7000m, item.BaseGross);
        await svc.ChangeAsync(item.Id, null, 0, null);                                         // świadome usunięcie choroby
        Assert.Equal(0, item.SickDays);
        Assert.Equal(2, item.UnpaidDays);
        Assert.Equal(7000m, item.BaseGross);
    }

    // ---------- PK-07: wynagrodzenie bazowe 0 to wartość, nie brak ----------
    [Fact]
    public async Task Zero_base_gross_is_kept_when_other_contract_of_person_is_recalculated()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new PayrollService(t.Db, t.User, t.Clock);
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ewa", LastName = "D", Pesel = Pesel, ContractType = ContractType.UmowaOPrace, Gross = 4806m, StartDate = Start });
        await svc.AddEmployeeAsync(new Employee { FirstName = "Ewa", LastName = "D", Pesel = Pesel, ContractType = ContractType.UmowaZlecenie, Gross = 2000m, ContractTotalAmount = 2000m, StartDate = Start });
        var list = await svc.CreateListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        var civil = list.Items.Single(i => i.ContractType == ContractType.UmowaZlecenie);
        var employment = list.Items.Single(i => i.ContractType == ContractType.UmowaOPrace);
        await svc.ChangeGrossAsync(civil.Id, 0m);                                               // w tym miesiącu zlecenie niewykonywane
        await svc.ChangeGrossAsync(employment.Id, 5000m);                                      // przeliczenie osoby
        Assert.Equal(0m, civil.BaseGross);
        Assert.Equal(0m, civil.Gross);
        await svc.RecalculateAsync(list.Id);
        Assert.Equal(0m, civil.Gross);
        await svc.PostAsync(list.Id);
    }
}

/// <summary>Weryfikacja: równoległe zarachowanie wpłaty CIT przez dwóch operatorów.</summary>
public class CitConcurrencyTests
{
    [Fact]
    public async Task Concurrent_allocation_of_the_same_payment_is_rejected_without_losing_records()
    {
        await using var t = await TestBooks.CreateAsync();
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 2, 15), "WB/1", new("220-01", 1000m, 0), new("130-01", 0, 1000m)));
        await t.Db.SaveChangesAsync();
        var cit1 = new CitService(t.Db, t.User, t.Clock);
        var payment = (await cit1.CitPaymentsAsync()).Single();
        await cit1.RecordPaymentAllocationAsync(t.Year.Id, payment.LineId, 100m, 1, false, "e-US — karta rozliczeń");
        // drugi operator (osobne połączenie) odczytał rejestr przed zapisem pierwszego
        await using var db2 = TestBooks.Open(t.DbPath);
        var cit2 = new CitService(db2, t.User, t.Clock);
        Assert.Equal(900m, (await cit2.CitPaymentsAsync()).Single().Unallocated);
        await cit1.RecordPaymentAllocationAsync(t.Year.Id, payment.LineId, 900m, 1, false, "e-US — operator 1");
        var ex = await Assert.ThrowsAsync<DomainException>(() => cit2.RecordPaymentAllocationAsync(t.Year.Id, payment.LineId, 900m, 2, false, "e-US — operator 2"));
        Assert.Contains("innego operatora", ex.Message);
        await using var db3 = TestBooks.Open(t.DbPath);
        var after = (await new CitService(db3, t.User, t.Clock).CitPaymentsAsync()).Single();
        Assert.Equal(0m, after.Unallocated);                                                    // brak podwójnego zarachowania
        Assert.Equal(2, (await new CitService(db3, t.User, t.Clock).PaymentAllocationHistoryAsync()).Count(a => !a.Cancelled)); // zapis operatora 1 nie zginął
    }
}
