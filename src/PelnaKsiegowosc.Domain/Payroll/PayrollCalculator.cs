namespace PelnaKsiegowosc.Domain.Payroll;

/// <summary>Parametry płacowe na rok (stawki procentowe w %). Wartości 2026 — zob. rejestr przepisów (sekcja Przepisy).</summary>
public sealed record PayrollRules(
    int Year,
    decimal MinimumWage,
    decimal AnnualPensionCap,
    decimal PensionEmployee, decimal PensionEmployer,
    decimal DisabilityEmployee, decimal DisabilityEmployer,
    decimal Sickness, decimal Health,
    decimal LaborFund, decimal Fgsp,
    decimal TaxRate1, decimal TaxRate2, decimal TaxThreshold,
    decimal MonthlyTaxReduction,
    decimal CostsStandard, decimal CostsIncreased,
    decimal YouthReliefLimit, decimal FlatRateLimit, decimal FlatRate)
{
    public const string Version = "PLACE-2026.1";

    public static PayrollRules Y2026 { get; } = new(2026,
        MinimumWage: 4806m, AnnualPensionCap: 282600m,
        PensionEmployee: 9.76m, PensionEmployer: 9.76m,
        DisabilityEmployee: 1.5m, DisabilityEmployer: 6.5m,
        Sickness: 2.45m, Health: 9m,
        LaborFund: 2.45m, Fgsp: 0.10m,
        TaxRate1: 12m, TaxRate2: 32m, TaxThreshold: 120000m,
        MonthlyTaxReduction: 300m,
        CostsStandard: 250m, CostsIncreased: 300m,
        YouthReliefLimit: 85528m, FlatRateLimit: 200m, FlatRate: 12m);

    /// <summary>Parametry dla roku wypłaty; dla lat bez zweryfikowanych parametrów stosuje się 2026 (z ostrzeżeniem).</summary>
    public static PayrollRules For(int year) => Y2026;
}

/// <summary>
/// Nieobecności w miesiącu (umowa o pracę): choroba — potrącenie 1/30 wynagrodzenia za dzień i wynagrodzenie chorobowe 80%
/// podstawy (wynagrodzenie pomniejszone o 13,71% składek) / 30 za dzień; nieobecność nieodpłatna — potrącenie proporcjonalne
/// do dni roboczych miesiąca.
/// </summary>
public sealed record PayrollAbsence(int SickDays, int UnpaidWorkingDays, int WorkingDaysInMonth, decimal? SickBasis = null, decimal SickRatePercent = 80m);

/// <summary>Narastające wartości od początku roku (z wcześniejszych list płac danej osoby).</summary>
public sealed record PayrollYearToDate(decimal PensionBasis = 0, decimal TaxBase = 0, decimal YouthExemptRevenue = 0);

/// <summary>
/// Wyliczenie wynagrodzenia: składki ZUS (pracownik i pracodawca, limit 30-krotności), składka zdrowotna, koszty uzyskania,
/// zaliczka na PIT (skala 12%/32%, kwota zmniejszająca z PIT-2, ulga dla młodych, ryczałt do 200 zł). Kwoty składek
/// zaokrąglane do groszy, podstawa i zaliczka podatku — do pełnych złotych.
/// </summary>
public static class PayrollCalculator
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static decimal R0(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);
    private static decimal Pct(decimal basis, decimal rate) => R2(basis * rate / 100m);

    public static PayrollItem Compute(Employee e, decimal gross, PayrollYearToDate ytd, PayrollRules rules, decimal accidentRate, PayrollAbsence? absence = null)
    {
        if (gross < 0) throw new Common.DomainException("Kwota wynagrodzenia nie może być ujemna.");
        var it = new PayrollItem { EmployeeId = e.Id, EmployeeName = e.FullName, ContractType = e.ContractType };
        var notes = new List<string>();
        var t = e.ContractType;
        var salary = gross;
        if (absence is { } a && t == ContractType.UmowaOPrace && (a.SickDays > 0 || a.UnpaidWorkingDays > 0))
        {
            if (a.SickDays is < 0 or > 31 || a.UnpaidWorkingDays < 0 || a.WorkingDaysInMonth <= 0 || a.UnpaidWorkingDays > a.WorkingDaysInMonth)
                throw new Common.DomainException("Nieprawidłowa liczba dni nieobecności.");
            var sickDeduction = a.SickDays >= 30 ? gross : R2(gross / 30m * a.SickDays);
            var unpaidDeduction = R2(gross / a.WorkingDaysInMonth * a.UnpaidWorkingDays);
            salary = Math.Max(0, gross - sickDeduction - unpaidDeduction);
            var sickBasis = (a.SickBasis ?? gross) * (1 - 0.1371m);
            it.SickPay = R2(sickBasis * a.SickRatePercent / 100m / 30m * a.SickDays);
            it.SickDays = a.SickDays;
            it.UnpaidDays = a.UnpaidWorkingDays;
            if (a.SickDays > 0) notes.Add($"choroba {a.SickDays} dni: wynagrodzenie chorobowe {it.SickPay:N2} zł (pierwsze 33 dni w roku płaci pracodawca, potem zasiłek ZUS)");
            if (a.UnpaidWorkingDays > 0) notes.Add($"nieobecność nieodpłatna {a.UnpaidWorkingDays} dni roboczych");
        }
        gross = salary + it.SickPay;
        it.Gross = gross;

        var social = t == ContractType.UmowaOPrace || (t == ContractType.UmowaZlecenie && !e.StudentUnder26);
        var healthDue = social || t == ContractType.Powolanie;
        var basis = social ? salary : 0m;
        it.SocialBasis = basis;
        var cappedBasis = Math.Min(basis, Math.Max(0, rules.AnnualPensionCap - ytd.PensionBasis));
        if (cappedBasis < basis) notes.Add("przekroczony limit 30-krotności — bez składek emerytalnej i rentowych od nadwyżki");

        it.PensionEmployee = Pct(cappedBasis, rules.PensionEmployee);
        it.DisabilityEmployee = Pct(cappedBasis, rules.DisabilityEmployee);
        it.SicknessEmployee = t == ContractType.UmowaOPrace || (social && e.VoluntarySickness) ? Pct(basis, rules.Sickness) : 0;
        it.HealthBase = healthDue ? R2(gross - it.SocialEmployee) : 0;
        it.Health = Pct(it.HealthBase, rules.Health);

        // ryczałt od umów zlecenia/o dzieło do 200 zł (art. 30 ust. 1 pkt 5a ustawy o PIT)
        if (t is ContractType.UmowaZlecenie or ContractType.UmowaODzielo && gross <= rules.FlatRateLimit && !e.Pit2 && !e.YouthRelief)
        {
            it.FlatRateTax = true;
            it.TaxBase = R0(gross);
            it.TaxAdvance = R0(it.TaxBase * rules.FlatRate / 100m);
            notes.Add("podatek zryczałtowany (umowa do 200 zł) — PIT-8AR");
        }
        else
        {
            // PPK: wpłata podstawowa pracodawcy jest przychodem pracownika (art. 12 ust. 1 ustawy o PIT)
            if (e.Ppk && social)
            {
                it.PpkEmployee = Pct(basis, e.PpkEmployeeRate);
                it.PpkEmployer = Pct(basis, e.PpkEmployerRate);
            }
            var taxableGross = gross + it.PpkEmployer;
            if (e.YouthRelief && t is ContractType.UmowaOPrace or ContractType.UmowaZlecenie)
            {
                var exempt = Math.Min(taxableGross, Math.Max(0, rules.YouthReliefLimit - ytd.YouthExemptRevenue));
                taxableGross -= exempt;
                it.ExemptRevenue = exempt;
                if (exempt > 0) notes.Add($"ulga dla młodych: zwolnione {exempt:N2} zł");
            }
            var share = gross + it.PpkEmployer == 0 ? 0 : taxableGross / (gross + it.PpkEmployer);
            var socialDeductible = R2(it.SocialEmployee * share);
            it.CostsOfIncome = taxableGross == 0 ? 0 : e.Costs switch
            {
                CostsOfIncome.Brak => 0,
                CostsOfIncome.Autorskie50 => R2((taxableGross - socialDeductible) * 0.5m),
                _ when t is ContractType.UmowaZlecenie or ContractType.UmowaODzielo => R2((taxableGross - socialDeductible) * 0.2m),
                CostsOfIncome.Podwyzszone => rules.CostsIncreased,
                _ => rules.CostsStandard
            };
            var income = Math.Max(0, taxableGross - it.CostsOfIncome);
            it.TaxBase = Math.Max(0, R0(income - socialDeductible));
            var lowPart = Math.Max(0, Math.Min(it.TaxBase, rules.TaxThreshold - ytd.TaxBase));
            var tax = lowPart * rules.TaxRate1 / 100m + (it.TaxBase - lowPart) * rules.TaxRate2 / 100m;
            if (it.TaxBase - lowPart > 0) notes.Add("przekroczony próg 120 000 zł — stawka 32%");
            if (e.Pit2 && it.TaxBase > 0) tax -= rules.MonthlyTaxReduction;
            it.TaxAdvance = Math.Max(0, R0(tax));
        }
        it.Net = R2(gross - it.SocialEmployee - it.Health - it.TaxAdvance - it.PpkEmployee);

        it.PensionEmployer = Pct(cappedBasis, rules.PensionEmployer);
        it.DisabilityEmployer = Pct(cappedBasis, rules.DisabilityEmployer);
        it.Accident = Pct(basis, accidentRate);
        var fpDue = social && !e.ExemptFromFp && (basis >= rules.MinimumWage * (t == ContractType.UmowaOPrace ? e.WorkTimeFraction : 1m));
        it.LaborFund = fpDue ? Pct(basis, rules.LaborFund) : 0;
        it.Fgsp = social && !e.ExemptFromFp ? Pct(basis, rules.Fgsp) : 0;
        if (t == ContractType.UmowaOPrace && absence is null or { SickDays: 0, UnpaidWorkingDays: 0 } && salary < rules.MinimumWage * e.WorkTimeFraction)
            notes.Add($"wynagrodzenie poniżej minimalnego ({rules.MinimumWage:N2} zł × etat) — sprawdź (np. niepełny miesiąc)");
        it.Notes = notes.Count == 0 ? null : string.Join("; ", notes);
        return it;
    }
}
