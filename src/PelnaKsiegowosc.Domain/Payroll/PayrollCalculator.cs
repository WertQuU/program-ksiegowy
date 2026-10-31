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

    /// <summary>2025: płaca minimalna 4666 zł, limit 30-krotności 260 190 zł; stawki i progi jak w 2026.</summary>
    public static PayrollRules Y2025 { get; } = Y2026 with { Year = 2025, MinimumWage = 4666m, AnnualPensionCap = 260190m };

    /// <summary>Parametry dla roku wypłaty. Dla roku bez zweryfikowanych parametrów — błąd (nie liczymy płac na nieaktualnych stawkach).</summary>
    public static PayrollRules For(int year) => year switch
    {
        2025 => Y2025,
        2026 => Y2026,
        _ => throw new Common.DomainException($"Brak zweryfikowanych parametrów płacowych na rok {year} (płaca minimalna, limit składek, stawki). " +
                                              "Zaktualizuj program przed sporządzeniem listy płac za ten rok — sprawdź sekcję Przepisy.")
    };

    /// <summary>Limit dni wynagrodzenia chorobowego płaconego przez pracodawcę w roku (art. 92 KP): 33 dni, 14 dni od roku następującego po roku ukończenia 50 lat.</summary>
    public static int EmployerSickDaysLimit(DateOnly? birthDate, int year) => birthDate is { } b && year > b.Year + 50 ? 14 : 33;
}

/// <summary>
/// Nieobecności w miesiącu (umowa o pracę): choroba — potrącenie 1/30 wynagrodzenia za dzień i wynagrodzenie chorobowe 80%
/// podstawy (wynagrodzenie pomniejszone o 13,71% składek) / 30 za dzień; nieobecność nieodpłatna — potrącenie proporcjonalne
/// do dni roboczych miesiąca.
/// </summary>
public sealed record PayrollAbsence(int SickDays, int UnpaidWorkingDays, int WorkingDaysInMonth, decimal? SickBasis = null, decimal SickRatePercent = 80m);

/// <summary>Narastające wartości od początku roku (z wcześniejszych list płac danej osoby).</summary>
/// <param name="AuthorCosts">Koszty 50% (art. 22 ust. 9 pkt 1–3 ustawy o PIT) uwzględnione od początku roku — limit roczny.</param>
public sealed record PayrollYearToDate(decimal PensionBasis = 0, decimal TaxBase = 0, decimal YouthExemptRevenue = 0, int EmployerSickDays = 0, decimal AuthorCosts = 0);

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

    /// <param name="otherTitlesFpBasis">Podstawy składek z innych tytułów tej samej osoby u tego płatnika w miesiącu (np. umowa o pracę
    /// i umowa zlecenia) — obowiązek opłacania Funduszu Pracy ustala się od łącznej podstawy.</param>
    /// <param name="ownEmployee">Zleceniobiorca / wykonawca dzieła jest jednocześnie pracownikiem płatnika (umowa o pracę) — ryczałt
    /// z art. 30 ust. 1 pkt 5a nie ma zastosowania.</param>
    public static PayrollItem Compute(Employee e, decimal gross, PayrollYearToDate ytd, PayrollRules rules, decimal accidentRate, PayrollAbsence? absence = null,
        decimal otherTitlesFpBasis = 0m, bool ownEmployee = false)
    {
        if (gross < 0) throw new Common.DomainException("Kwota wynagrodzenia nie może być ujemna.");
        var fullMonthGross = gross;
        var it = new PayrollItem { EmployeeId = e.Id, EmployeeName = e.FullName, ContractType = e.ContractType, BaseGross = gross };
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
            // art. 92 KP: wynagrodzenie chorobowe płaci pracodawca do 33 dni w roku (14 dni — pracownik po 50. roku życia); dalej zasiłek ZUS
            var limit = PayrollRules.EmployerSickDaysLimit(e.BirthDate ?? Validation.Identifiers.BirthDateFromPesel(e.Pesel), rules.Year);
            var employerDays = Math.Clamp(limit - ytd.EmployerSickDays, 0, a.SickDays);
            it.SickPay = R2(sickBasis * a.SickRatePercent / 100m / 30m * employerDays);
            it.SickDays = a.SickDays;
            it.SickDaysEmployer = employerDays;
            it.UnpaidDays = a.UnpaidWorkingDays;
            if (a.SickDays > 0) notes.Add($"choroba {a.SickDays} dni: wynagrodzenie chorobowe za {employerDays} dni {it.SickPay:N2} zł (limit pracodawcy {limit} dni w roku, wykorzystano wcześniej {ytd.EmployerSickDays})");
            if (a.SickDays > employerDays)
                notes.Add($"ZASIŁEK CHOROBOWY za {a.SickDays - employerDays} dni — wypłaca ZUS (zgłoś Z-3) lub płatnik zgłaszający do ubezpieczenia chorobowego ponad 20 osób; nie jest wliczany do tej listy");
            if (a.UnpaidWorkingDays > 0) notes.Add($"nieobecność nieodpłatna {a.UnpaidWorkingDays} dni roboczych");
        }
        gross = salary + it.SickPay;
        it.Gross = gross;

        // art. 8 ust. 2a ustawy o sus: umowa zlecenia lub o dzieło zawarta z własnym pracownikiem albo wykonywana na rzecz
        // pracodawcy — wykonawca jest dla celów ubezpieczeń pracownikiem: składki jak od stosunku pracy (emerytalna, rentowe,
        // chorobowa obowiązkowa, wypadkowa, FP/FGŚP, zdrowotna), bez zwolnienia studenta do 26 lat; podstawa łączna z wynagrodzeniem
        var employeeLike = t is ContractType.UmowaZlecenie or ContractType.UmowaODzielo && (ownEmployee || e.WorkForOwnEmployer);
        if (employeeLike) notes.Add("art. 8 ust. 2a ustawy o sus — umowa z własnym pracownikiem / na rzecz pracodawcy: składki jak od stosunku pracy");
        var social = t == ContractType.UmowaOPrace || employeeLike || (t == ContractType.UmowaZlecenie && !e.StudentUnder26);
        var healthDue = social || t == ContractType.Powolanie;
        var basis = social ? salary : 0m;
        it.SocialBasis = basis;
        var cappedBasis = Math.Min(basis, Math.Max(0, rules.AnnualPensionCap - ytd.PensionBasis));
        if (cappedBasis < basis) notes.Add("przekroczony limit 30-krotności — bez składek emerytalnej i rentowych od nadwyżki");

        it.PensionEmployee = Pct(cappedBasis, rules.PensionEmployee);
        it.DisabilityEmployee = Pct(cappedBasis, rules.DisabilityEmployee);
        it.SicknessEmployee = t == ContractType.UmowaOPrace || employeeLike || (social && e.VoluntarySickness) ? Pct(basis, rules.Sickness) : 0;
        it.HealthBase = healthDue ? R2(gross - it.SocialEmployee) : 0;
        it.Health = Pct(it.HealthBase, rules.Health);

        // ryczałt od umów zlecenia/o dzieło (art. 30 ust. 1 pkt 5a ustawy o PIT): kwota należności OKREŚLONA W UMOWIE nie przekracza
        // 200 zł i umowa zawarta z osobą niebędącą pracownikiem płatnika. Ryczałt obowiązuje niezależnie od wieku — zwolnienie dla
        // młodych (art. 21 ust. 1 pkt 148) nie obejmuje przychodów opodatkowanych na podstawie art. 30 ust. 1 pkt 5a. Bez kwoty
        // z umowy ryczałtu nie stosuje się domyślnie (lista płac nie może zostać zaksięgowana do czasu jej uzupełnienia).
        var civil = t is ContractType.UmowaZlecenie or ContractType.UmowaODzielo;
        if (civil && e.ContractTotalAmount is null)
            notes.Add("BRAK KWOTY NALEŻNOŚCI Z UMOWY — sposobu opodatkowania (ryczałt do 200 zł / zasady ogólne) nie można ustalić; uzupełnij kwotę z umowy przed zaksięgowaniem listy");
        var flatRate = civil && e.ContractTotalAmount is { } contractAmount && contractAmount <= rules.FlatRateLimit;
        if (flatRate && ownEmployee)
        {
            notes.Add("umowa z własnym pracownikiem — ryczałt z art. 30 ust. 1 pkt 5a nie ma zastosowania (zaliczka na zasadach ogólnych)");
            flatRate = false;
        }
        if (flatRate)
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
            decimal Regular(decimal basePart) => t is ContractType.UmowaZlecenie or ContractType.UmowaODzielo ? R2(basePart * 0.2m)
                : e.Costs == CostsOfIncome.Podwyzszone ? rules.CostsIncreased : rules.CostsStandard;
            if (taxableGross == 0 || e.Costs == CostsOfIncome.Brak) it.CostsOfIncome = 0;
            else if (e.Costs == CostsOfIncome.Autorskie50)
            {
                // koszty 50% tylko od części wynagrodzenia za korzystanie z praw autorskich / rozporządzanie nimi (udział z umowy);
                // limit roczny 120 000 zł (art. 22 ust. 9a); przy uldze dla młodych suma kosztów 50% i przychodów zwolnionych nie
                // przekracza limitu zwolnienia (art. 22 ust. 9aa); pozostała część — koszty zwykłe
                var authorShare = Math.Clamp(e.AuthorSharePercent, 0m, 100m) / 100m;
                var costBasis = taxableGross - socialDeductible;
                var authorPart = R2(costBasis * authorShare);
                var available = Math.Max(0, rules.TaxThreshold - ytd.AuthorCosts);
                if (it.ExemptRevenue > 0 || ytd.YouthExemptRevenue > 0)
                    available = Math.Min(available, Math.Max(0, rules.YouthReliefLimit - ytd.AuthorCosts - ytd.YouthExemptRevenue - it.ExemptRevenue));
                var author = Math.Min(R2(authorPart * 0.5m), available);
                if (author < R2(authorPart * 0.5m)) notes.Add($"limit kosztów 50% (art. 22 ust. 9a–9aa ustawy o PIT) — uwzględniono {author:N2} zł");
                it.AuthorCosts = author;
                it.CostsOfIncome = author + (authorShare < 1m || author < R2(authorPart * 0.5m) ? Regular(costBasis - authorPart) : 0m);
            }
            else it.CostsOfIncome = Regular(taxableGross - socialDeductible);
            var income = Math.Max(0, taxableGross - it.CostsOfIncome);
            it.TaxBase = Math.Max(0, R0(income - socialDeductible));
            // skala (próg 120 000 zł) przy zaliczkach — tylko ze stosunku pracy (art. 32 ustawy o PIT); zlecenie, dzieło, powołanie
            // (art. 13 pkt 7–8) — zaliczka według najniższej stawki skali (art. 41 ust. 1), bez progu
            var lowPart = t == ContractType.UmowaOPrace ? Math.Max(0, Math.Min(it.TaxBase, rules.TaxThreshold - ytd.TaxBase)) : it.TaxBase;
            var tax = lowPart * rules.TaxRate1 / 100m + (it.TaxBase - lowPart) * rules.TaxRate2 / 100m;
            if (it.TaxBase - lowPart > 0) notes.Add("przekroczony próg 120 000 zł — stawka 32%");
            if (e.Pit2 && it.TaxBase > 0) tax -= rules.MonthlyTaxReduction;
            it.TaxAdvance = Math.Max(0, R0(tax));
        }
        it.Net = R2(gross - it.SocialEmployee - it.Health - it.TaxAdvance - it.PpkEmployee);

        it.PensionEmployer = Pct(cappedBasis, rules.PensionEmployer);
        it.DisabilityEmployer = Pct(cappedBasis, rules.DisabilityEmployer);
        it.Accident = Pct(basis, accidentRate);
        // Fundusz Pracy: podstawa wymiaru (łącznie z innymi tytułami u płatnika) w przeliczeniu na okres miesiąca co najmniej równa
        // PEŁNEMU minimalnemu wynagrodzeniu — próg nie jest proporcjonalny do wymiaru etatu; przy nieobecności (choroba, urlop
        // bezpłatny) porównuje się wynagrodzenie za pełny miesiąc
        var fpComparison = (t == ContractType.UmowaOPrace && absence is { } ab && (ab.SickDays > 0 || ab.UnpaidWorkingDays > 0) ? fullMonthGross : basis) + otherTitlesFpBasis;
        var fpDue = social && !e.ExemptFromFp && fpComparison >= rules.MinimumWage;
        it.LaborFund = fpDue ? Pct(basis, rules.LaborFund) : 0;
        if (social && !e.ExemptFromFp && !fpDue)
            notes.Add($"bez Funduszu Pracy — podstawa {fpComparison:N2} zł (z innymi tytułami, w przeliczeniu na miesiąc) poniżej minimalnego wynagrodzenia {rules.MinimumWage:N2} zł");
        it.Fgsp = social && !e.ExemptFromFp ? Pct(basis, rules.Fgsp) : 0;
        if (t == ContractType.UmowaOPrace && absence is null or { SickDays: 0, UnpaidWorkingDays: 0 } && salary < rules.MinimumWage * e.WorkTimeFraction)
            notes.Add($"wynagrodzenie poniżej minimalnego ({rules.MinimumWage:N2} zł × etat) — sprawdź (np. niepełny miesiąc)");
        it.Notes = notes.Count == 0 ? null : string.Join("; ", notes);
        return it;
    }
}
