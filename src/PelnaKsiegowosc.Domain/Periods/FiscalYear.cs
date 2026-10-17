using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Periods;

public enum FiscalYearStatus
{
    /// <summary>Otwarty — można księgować.</summary>
    Otwarty,
    /// <summary>W trakcie zamknięcia — wykonano zapisy zamknięcia, oczekuje zatwierdzenia.</summary>
    WZamknieciu,
    /// <summary>Zamknięty — księgi zamknięte (art. 12 ust. 2 UoR); brak zmian.</summary>
    Zamkniety,
    /// <summary>Zarchiwizowany.</summary>
    Zarchiwizowany
}

public enum PeriodStatus
{
    Otwarty,
    Zamkniety
}

/// <summary>Rok obrotowy (art. 3 ust. 1 pkt 9 UoR) — rok kalendarzowy lub inny okres trwający 12 miesięcy; pierwszy może być dłuższy (do 24 mies.) lub krótszy.</summary>
public class FiscalYear : CompanyEntity
{
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public FiscalYearStatus Status { get; set; } = FiscalYearStatus.Otwarty;
    public bool IsFirstYear { get; set; }

    /// <summary>Czy wprowadzono i zatwierdzono bilans otwarcia (BO).</summary>
    public bool OpeningBalanceApproved { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public Guid? PreviousYearId { get; set; }

    public List<AccountingPeriod> Periods { get; set; } = new();

    public bool Contains(DateOnly d) => d >= StartDate && d <= EndDate;

    public static ValidationResult ValidateRange(DateOnly start, DateOnly end, bool isFirstYear)
    {
        var r = new ValidationResult();
        if (end <= start) { r.Error("Data końca roku obrotowego musi być późniejsza niż data początku."); return r; }
        if (end.AddDays(1).Day != 1) r.Error("Rok obrotowy musi kończyć się ostatnim dniem miesiąca.");
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month + 1;
        if (!isFirstYear)
        {
            if (start.Day != 1) r.Error("Rok obrotowy musi zaczynać się pierwszego dnia miesiąca.");
            if (months != 12) r.Error("Rok obrotowy (inny niż pierwszy) musi obejmować 12 kolejnych miesięcy (art. 3 ust. 1 pkt 9 UoR).");
        }
        else if (months > 24)
        {
            r.Error("Pierwszy rok obrotowy nie może przekraczać 24 miesięcy (art. 3 ust. 1 pkt 9 UoR — gdy działalność rozpoczęto w drugiej połowie roku).");
        }
        return r;
    }

    /// <summary>Tworzy okresy miesięczne (okresy sprawozdawcze — art. 3 ust. 1 pkt 10 UoR) dla zakresu roku.</summary>
    public void GenerateMonthlyPeriods()
    {
        Periods.Clear();
        var cursor = StartDate;
        var n = 1;
        while (cursor <= EndDate)
        {
            var monthEnd = new DateOnly(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month));
            if (monthEnd > EndDate) monthEnd = EndDate;
            Periods.Add(new AccountingPeriod
            {
                CompanyId = CompanyId,
                FiscalYearId = Id,
                Number = n++,
                StartDate = cursor,
                EndDate = monthEnd,
                Status = PeriodStatus.Otwarty
            });
            cursor = monthEnd.AddDays(1);
        }
    }
}

/// <summary>Okres sprawozdawczy (miesiąc). Zamknięcie blokuje księgowanie dowodów z datą w okresie.</summary>
public class AccountingPeriod : CompanyEntity
{
    public Guid FiscalYearId { get; set; }
    public int Number { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PeriodStatus Status { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public int ReopenCount { get; set; }

    public string Name => $"{StartDate:MM}/{StartDate:yyyy}";
    public bool Contains(DateOnly d) => d >= StartDate && d <= EndDate;
}

/// <summary>Historia zamknięć / ponownych otwarć okresu wraz z uzasadnieniem.</summary>
public class PeriodStatusChange : CompanyEntity
{
    public Guid PeriodId { get; set; }
    public PeriodStatus FromStatus { get; set; }
    public PeriodStatus ToStatus { get; set; }
    public Guid UserId { get; set; }
    public DateTime AtUtc { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Wynik kontroli zamknięcia (JSON) w chwili zmiany.</summary>
    public string? ChecklistJson { get; set; }
}
