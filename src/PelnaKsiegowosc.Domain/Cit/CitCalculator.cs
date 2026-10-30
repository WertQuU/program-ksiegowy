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
    /// <summary>Ręczna korekta art. 18f: zobowiązanie źródłowe. Wyklucza równoległe rozliczenie automatyczne.</summary>
    public Guid? OpenItemId { get; set; }
    public bool CapitalGains { get; set; }
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

public enum CitLiabilityAdjustmentKind
{
    /// <summary>Zwiększenie dochodu — zobowiązanie niezapłacone 90 dni po terminie (art. 18f ust. 1 pkt 2).</summary>
    Zwiekszenie,
    /// <summary>Zmniejszenie — zapłata zobowiązania, o które wcześniej zwiększono dochód.</summary>
    Zmniejszenie
}

/// <summary>
/// Historia korekt z art. 18f ustawy o CIT dla konkretnego zobowiązania: kwoty osobno dla źródła „inne” i „zyski
/// kapitałowe”. Zapisywana przy zapisie wyliczenia zaliczki (stan narastająco roku); zmniejszenie po zapłacie w kolejnych
/// latach jest możliwe tylko do wysokości wcześniej zapisanych zwiększeń.
/// </summary>
public class CitLiabilityAdjustment : CompanyEntity
{
    public Guid FiscalYearId { get; set; }
    public Guid OpenItemId { get; set; }
    public string Reference { get; set; } = "";
    public CitLiabilityAdjustmentKind Kind { get; set; }
    /// <summary>Dzień, w którym powstał obowiązek korekty (91. dzień po terminie albo dzień zapłaty).</summary>
    public DateOnly Date { get; set; }
    public decimal AmountOperating { get; set; }
    public decimal AmountCapital { get; set; }
    public DateTime RecordedAtUtc { get; set; }
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

public sealed record ReducedRateDecision(decimal RatePercent, decimal LimitPln, string Reason, bool RateMissing);

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

    /// <summary>Limit przychodów dla stawki 9% (art. 19 ust. 1 pkt 2) i statusu małego podatnika (art. 4a pkt 10): 2 000 000 euro.</summary>
    public const decimal ReducedRateLimitEur = 2_000_000m;

    /// <summary>Pełne miesiące kalendarzowe oraz pełne okresy 30 dni z pozostałych fragmentów roku.</summary>
    public static int FullMonths(DateOnly start, DateOnly end)
    {
        if (end < start) throw new DomainException("Koniec roku poprzedza jego początek.");
        var months = 0;
        var remainingDays = end.DayNumber - start.DayNumber + 1;
        var month = new DateOnly(start.Year, start.Month, 1);
        while (month <= end)
        {
            var days = DateTime.DaysInMonth(month.Year, month.Month);
            if (month >= start && month.AddDays(days - 1) <= end)
            {
                months++;
                remainingDays -= days;
            }
            if (month.Year == 9999 && month.Month == 12) break;
            month = month.AddMonths(1);
        }
        return months + remainingDays / 30;
    }

    /// <summary>
    /// Limit przychodów roku podatkowego dla stawki 9% (art. 19 ust. 1 pkt 2): 2 000 000 euro — a gdy rok podatkowy jest
    /// krótszy lub dłuższy niż 12 miesięcy, 1/12 tej kwoty za każdy pełny miesiąc roku — przeliczone po średnim kursie euro
    /// NBP ogłoszonym na pierwszy dzień roboczy roku podatkowego i dopiero wtedy zaokrąglone do 1000 zł.
    /// </summary>
    public static decimal ReducedRateLimitPln(decimal eurRateFirstBusinessDayOfTaxYear, int fullMonthsOfTaxYear)
    {
        if (eurRateFirstBusinessDayOfTaxYear <= 0) throw new DomainException("Kurs euro musi być dodatni.");
        if (fullMonthsOfTaxYear <= 0) throw new DomainException("Rok podatkowy nie obejmuje pełnego miesiąca.");
        var eur = fullMonthsOfTaxYear == 12 ? ReducedRateLimitEur : ReducedRateLimitEur / 12m * fullMonthsOfTaxYear;
        return RoundToThousand(eur * eurRateFirstBusinessDayOfTaxYear);
    }

    /// <summary>Limit statusu małego podatnika (art. 4a pkt 10): przychody ze sprzedaży z VAT w poprzednim roku podatkowym ≤ 2 mln euro po kursie NBP z pierwszego dnia roboczego października tamtego roku, zaokr. do 1000 zł.</summary>
    public static decimal SmallTaxpayerLimitPln(decimal eurRateFirstBusinessDayOfOctober)
    {
        if (eurRateFirstBusinessDayOfOctober <= 0) throw new DomainException("Kurs euro musi być dodatni.");
        return RoundToThousand(ReducedRateLimitEur * eurRateFirstBusinessDayOfOctober);
    }

    private static decimal RoundToThousand(decimal v) => Math.Round(v / 1000m, 0, MidpointRounding.AwayFromZero) * 1000m;

    /// <summary>
    /// Stawka dla dochodu z innych źródeł niż zyski kapitałowe (art. 19 ust. 1 pkt 1–2): 9% tylko gdy podatnik jest małym
    /// podatnikiem albo rozpoczyna działalność, nie podlega wyłączeniom (art. 19 ust. 1b–1c) ORAZ przychody osiągnięte w roku
    /// podatkowym (narastająco) nie przekroczyły limitu. Po przekroczeniu limitu — 19% od dochodu narastająco.
    /// Bez kursu NBP z pierwszego dnia roboczego roku podatkowego limitu nie da się ustalić: wynik jest wstępny
    /// (<see cref="ReducedRateDecision.RateMissing"/>) i nie może być podstawą zapisu zaliczki.
    /// </summary>
    public static ReducedRateDecision DecideRate(bool smallTaxpayerOrFirstYear, bool excluded, decimal? eurRate, int fullMonths, decimal revenueToDate)
    {
        if (!smallTaxpayerOrFirstYear)
            return new(StandardRate, 0m, "19% — podatnik nie jest małym podatnikiem (art. 4a pkt 10) ani nie rozpoczyna działalności.", false);
        if (excluded)
            return new(StandardRate, 0m, "19% — wyłączenie z art. 19 ust. 1b–1c (np. podmiot powstały z przekształcenia, połączenia, podziału lub wniesienia przedsiębiorstwa).", false);
        if (eurRate is null)
            return new(ReducedRate, 0m, "9% WSTĘPNIE — brak kursu euro NBP z pierwszego dnia roboczego roku podatkowego; limitu przychodów (art. 19 ust. 1 pkt 2) nie ustalono.", true);
        var limit = ReducedRateLimitPln(eurRate.Value, fullMonths);
        if (revenueToDate > limit)
            return new(StandardRate, limit, $"19% — przychody roku podatkowego ({Money.Format(revenueToDate)}) przekroczyły limit {Money.Format(limit)} (art. 19 ust. 1 pkt 2).", false);
        return new(ReducedRate, limit, $"9% — mały podatnik / rozpoczynający działalność, przychody roku {Money.Format(revenueToDate)} ≤ limit {Money.Format(limit)}.", false);
    }

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
    /// <summary>Liczba dni roboczych (pon.–pt. bez świąt) w miesiącu.</summary>
    public static int WorkingDays(int year, int month)
    {
        var n = 0;
        for (var d = new DateOnly(year, month, 1); d.Month == month; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(d)) n++;
        return n;
    }

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
