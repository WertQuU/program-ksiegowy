using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Domain.Settlements;

public enum OpenItemSide
{
    /// <summary>Należność (saldo Wn na koncie rozrachunkowym).</summary>
    Naleznosc,
    /// <summary>Zobowiązanie (saldo Ma).</summary>
    Zobowiazanie
}

public enum OpenItemStatus
{
    Otwarta,
    CzesciowoRozliczona,
    Rozliczona
}

/// <summary>Pozycja rozrachunkowa — powstaje przy księgowaniu dokumentu na koncie rozrachunkowym.</summary>
public class OpenItem : CompanyEntity
{
    /// <summary>Konto z roku powstania rozrachunku.</summary>
    public Guid AccountId { get; set; }
    /// <summary>Numer konta — plan kont jest wersjonowany per rok, więc rozrachunek wiąże się z kontem kolejnych lat po numerze.</summary>
    public string AccountNumber { get; set; } = "";
    public Guid CounterpartyId { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid JournalEntryId { get; set; }
    public OpenItemSide Side { get; set; }
    public string Reference { get; set; } = "";
    public DateOnly DocumentDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Currency { get; set; } = "PLN";
    public decimal Amount { get; set; }
    public decimal AmountPln { get; set; }
    public decimal SettledAmount { get; set; }
    public decimal SettledAmountPln { get; set; }
    public OpenItemStatus Status { get; set; } = OpenItemStatus.Otwarta;
    public string? Description { get; set; }

    public decimal RemainingPln => AmountPln - SettledAmountPln;
    public decimal Remaining => Amount - SettledAmount;

    public int DaysOverdue(DateOnly asOf) => DueDate is { } d && asOf > d && Status != OpenItemStatus.Rozliczona ? asOf.DayNumber - d.DayNumber : 0;

    public void ApplySettlement(decimal amount, decimal amountPln)
    {
        if (amount <= 0) throw new DomainException("Kwota rozliczenia musi być dodatnia.");
        if (amount > Remaining) throw new DomainException($"Kwota rozliczenia {Money.Format(amount)} przekracza pozostałą kwotę rozrachunku {Money.Format(Remaining)}.");
        SettledAmount += amount;
        SettledAmountPln += amountPln;
        RefreshStatus();
    }

    public void RevertSettlement(decimal amount, decimal amountPln)
    {
        SettledAmount -= amount;
        SettledAmountPln -= amountPln;
        if (SettledAmount < 0) throw new DomainException("Rozliczenie ujemne — niespójność danych rozrachunków.");
        RefreshStatus();
    }

    public void RefreshStatus() =>
        Status = SettledAmount == 0 ? OpenItemStatus.Otwarta : SettledAmount >= Amount ? OpenItemStatus.Rozliczona : OpenItemStatus.CzesciowoRozliczona;
}

public enum SettlementSource
{
    Bank,
    Kasa,
    Kompensata,
    PolecenieKsiegowania,
    Korekta
}

/// <summary>Rozliczenie (skojarzenie) pozycji rozrachunkowej z płatnością lub inną pozycją.</summary>
public class SettlementAllocation : CompanyEntity
{
    public Guid OpenItemId { get; set; }
    public SettlementSource Source { get; set; }
    public Guid? MoneyTransactionId { get; set; }
    /// <summary>Druga strona kompensaty / korekty.</summary>
    public Guid? CounterOpenItemId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public decimal AmountPln { get; set; }
    /// <summary>Różnica kursowa zrealizowana (PLN), jeżeli wystąpiła.</summary>
    public decimal ExchangeDifferencePln { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsReversed { get; set; }
}
