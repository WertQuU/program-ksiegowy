using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Cit;

/// <summary>Ręczna korekta wyniku do celów CIT (różnica między wynikiem bilansowym a podatkowym), udokumentowana opisem.</summary>
public class CitAdjustment : CompanyEntity
{
    public Guid FiscalYearId { get; set; }
    /// <summary>Miesiąc (1–12 w roku obrotowym), od którego korekta wpływa na zaliczki.</summary>
    public int PeriodNumber { get; set; }
    public CitAdjustmentType Type { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string? LegalBasis { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum CitAdjustmentType
{
    /// <summary>Przychód podatkowy niebędący przychodem bilansowym (+).</summary>
    ZwiekszeniePrzychodow,
    /// <summary>Przychód bilansowy niebędący przychodem podatkowym (−).</summary>
    ZmniejszeniePrzychodow,
    /// <summary>Koszt bilansowy niebędący KUP (+ do dochodu).</summary>
    KosztNKUP,
    /// <summary>KUP niebędący kosztem bilansowym (np. zapłacone odsetki, amortyzacja podatkowa wyższa) (−).</summary>
    DodatkowyKUP,
    /// <summary>Odliczenie od dochodu (np. strata z lat ubiegłych — art. 7 ust. 5, darowizny — art. 18).</summary>
    OdliczenieOdDochodu
}

/// <summary>Zaliczka CIT za okres (zapis wykonanego wyliczenia).</summary>
public class CitAdvance : CompanyEntity
{
    public Guid FiscalYearId { get; set; }
    public int PeriodNumber { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal CumulativeTaxRevenue { get; set; }
    public decimal CumulativeTaxCosts { get; set; }
    public decimal CumulativeIncome { get; set; }
    public decimal Deductions { get; set; }
    public decimal TaxBase { get; set; }
    public decimal RatePercent { get; set; }
    public decimal CumulativeTax { get; set; }
    public decimal PreviouslyDue { get; set; }
    public decimal AdvanceDue { get; set; }
    public DateOnly PaymentDeadline { get; set; }
    public bool IsApproved { get; set; }
    public Guid UserId { get; set; }
    public DateTime CalculatedAtUtc { get; set; }
    public string? WarningsText { get; set; }
}

public sealed record CitComputation(
    decimal TaxRevenue,
    decimal TaxCosts,
    decimal Income,
    decimal Loss,
    decimal Deductions,
    decimal TaxBase,
    decimal RatePercent,
    decimal Tax);

/// <summary>
/// Obliczenia CIT. Stawki: 19% albo 9% dla małych podatników i rozpoczynających działalność (art. 19 ust. 1 ustawy o CIT;
/// 9% nie dotyczy przychodów z zysków kapitałowych). Podstawa i podatek zaokrąglane do pełnych złotych (art. 63 § 1 Ordynacji).
/// Zaliczka: podatek od dochodu od początku roku minus zaliczki należne za poprzednie okresy (art. 25 ust. 1).
/// Termin wpłaty: do 20. dnia miesiąca następującego po okresie (art. 25 ust. 1).
/// </summary>
public static class CitCalculator
{
    public const decimal StandardRate = 19m;
    public const decimal ReducedRate = 9m;

    public static decimal RateFor(bool smallTaxpayerOrFirstYear) => smallTaxpayerOrFirstYear ? ReducedRate : StandardRate;

    public static CitComputation Compute(decimal taxRevenue, decimal taxCosts, decimal deductions, decimal ratePercent)
    {
        if (deductions < 0) throw new DomainException("Odliczenia nie mogą być ujemne.");
        var income = taxRevenue - taxCosts;
        var loss = income < 0 ? -income : 0m;
        var positive = Math.Max(0m, income);
        var afterDeductions = Math.Max(0m, positive - deductions);
        var taxBase = Money.RoundToFullZloty(afterDeductions);
        var tax = Money.RoundToFullZloty(taxBase * ratePercent / 100m);
        return new CitComputation(Money.Round(taxRevenue), Money.Round(taxCosts), Money.Round(positive), Money.Round(loss), deductions, taxBase, ratePercent, tax);
    }

    public static decimal AdvanceDue(decimal cumulativeTax, decimal previouslyDueAdvances) =>
        Money.RoundToFullZloty(Math.Max(0m, cumulativeTax - previouslyDueAdvances));

    public static DateOnly AdvanceDeadline(DateOnly periodEnd)
    {
        var next = periodEnd.AddDays(1);
        var d = new DateOnly(next.Year, next.Month, 20);
        return ShiftToBusinessDay(d);
    }

    /// <summary>Termin złożenia CIT-8: koniec trzeciego miesiąca roku następującego po roku podatkowym (art. 27 ust. 1).</summary>
    public static DateOnly AnnualReturnDeadline(DateOnly fiscalYearEnd)
    {
        var m = fiscalYearEnd.AddDays(1).AddMonths(3).AddDays(-1);
        return ShiftToBusinessDay(m);
    }

    /// <summary>Przesunięcie terminu przypadającego na sobotę/niedzielę/święto na następny dzień roboczy (art. 12 § 5 Ordynacji).</summary>
    public static DateOnly ShiftToBusinessDay(DateOnly d)
    {
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || PolishHolidays.IsHoliday(d)) d = d.AddDays(1);
        return d;
    }
}

/// <summary>Dni ustawowo wolne od pracy w Polsce (ustawa z 18.01.1951 r., w tym Wigilia od 2025 r.).</summary>
public static class PolishHolidays
{
    public static bool IsHoliday(DateOnly d)
    {
        var fixedDays = new[] { (1, 1), (1, 6), (5, 1), (5, 3), (8, 15), (11, 1), (11, 11), (12, 25), (12, 26) };
        if (fixedDays.Contains((d.Month, d.Day))) return true;
        if (d.Year >= 2025 && d.Month == 12 && d.Day == 24) return true;
        var easter = EasterSunday(d.Year);
        return d == easter || d == easter.AddDays(1) || d == easter.AddDays(49) || d == easter.AddDays(60);
    }

    public static DateOnly EasterSunday(int y)
    {
        var a = y % 19; var b = y / 100; var c = y % 100; var d = b / 4; var e = b % 4;
        var f = (b + 8) / 25; var g = (b - f + 1) / 3; var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4; var k = c % 4; var l = (32 + 2 * e + 2 * i - h - k) % 7; var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31; var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(y, month, day);
    }
}
