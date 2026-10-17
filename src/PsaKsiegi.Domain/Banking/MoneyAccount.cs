using System.Security.Cryptography;
using System.Text;
using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Domain.Banking;

public enum MoneyAccountKind
{
    RachunekBankowy,
    Kasa
}

/// <summary>Rachunek bankowy lub kasa jednostki, powiązany z kontem księgowym (np. 131-01, 101).</summary>
public class MoneyAccount : CompanyEntity
{
    public MoneyAccountKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Iban { get; set; }
    public string? BankName { get; set; }
    public string Currency { get; set; } = "PLN";
    /// <summary>Numer konta księgowego (syntetyka lub analityka).</summary>
    public string LedgerAccountNumber { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Format importu wyciągów (np. „CSV-GENERIC”, „MT940”).</summary>
    public string? ImportProfile { get; set; }
}

public enum StatementStatus
{
    Zaimportowany,
    WTrakcieRozliczania,
    Rozliczony
}

/// <summary>Wyciąg bankowy lub raport kasowy (zbiór operacji).</summary>
public class MoneyStatement : CompanyEntity
{
    public Guid MoneyAccountId { get; set; }
    public string Number { get; set; } = "";
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public string? SourceFileName { get; set; }
    public string? SourceFileSha256 { get; set; }
    public StatementStatus Status { get; set; }
    public DateTime ImportedAtUtc { get; set; }
    public Guid ImportedByUserId { get; set; }
    public Guid? DocumentId { get; set; }
}

public enum TransactionStatus
{
    Nierozliczona,
    Dopasowana,
    Zaksiegowana,
    Pominieta
}

/// <summary>Operacja bankowa/kasowa. Kwota dodatnia = wpływ, ujemna = wypływ.</summary>
public class MoneyTransaction : CompanyEntity
{
    public Guid MoneyAccountId { get; set; }
    public Guid? StatementId { get; set; }
    public DateOnly BookingDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PLN";
    public string? CounterpartyName { get; set; }
    public string? CounterpartyIban { get; set; }
    public string Title { get; set; } = "";
    public string? BankReference { get; set; }
    /// <summary>Skrót identyfikujący operację — ochrona przed ponownym importem (duplikaty).</summary>
    public string DedupHash { get; set; } = "";
    public TransactionStatus Status { get; set; } = TransactionStatus.Nierozliczona;
    public Guid? MatchedCounterpartyId { get; set; }
    public Guid? JournalEntryId { get; set; }
    /// <summary>Konto przeciwstawne dla operacji niebędących rozliczeniem (np. opłaty bankowe 640/ 760).</summary>
    public Guid? OffsetAccountId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public bool IsInflow => Amount > 0;

    public static string ComputeDedupHash(Guid moneyAccountId, DateOnly date, decimal amount, string? reference, string? counterpartyIban, string title)
    {
        var key = string.Join("|", moneyAccountId, date.ToString("yyyy-MM-dd"), Money.Xml(amount),
            (reference ?? "").Trim().ToUpperInvariant(), (counterpartyIban ?? "").Replace(" ", ""), title.Trim().ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
