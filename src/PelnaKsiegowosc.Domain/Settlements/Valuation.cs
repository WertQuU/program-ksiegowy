using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Settlements;

/// <summary>
/// Odpis aktualizujący należność (art. 35b UoR): kwota dodatnia — utworzenie/zwiększenie (Wn 761-02 / Ma 290),
/// ujemna — rozwiązanie (Wn 290 / Ma 760-04) albo wykorzystanie przy spisaniu należności (Wn 290 / Ma konto rozrachunku).
/// </summary>
public class ReceivableAllowance : CompanyEntity
{
    public Guid OpenItemId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public Guid? JournalEntryId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Rodzaj rezerwy (art. 35d UoR).</summary>
public enum ProvisionKind
{
    /// <summary>Niewykorzystane urlopy (świadczenie pracownicze).</summary>
    Urlopowa,
    /// <summary>Odprawy emerytalne, rentowe, nagrody jubileuszowe.</summary>
    Emerytalna,
    /// <summary>Koszty przypadające na okres, nieudokumentowane fakturą (np. badanie sprawozdania, media).</summary>
    NaKoszty,
    /// <summary>Gwarancje, naprawy gwarancyjne.</summary>
    Gwarancyjna,
    /// <summary>Sprawy sądowe, kary, inne pewne lub prawdopodobne zobowiązania.</summary>
    Inna
}

/// <summary>Rezerwa na zobowiązania: utworzenie (Wn koszt / Ma 840 lub 843), wykorzystanie i rozwiązanie (Wn rezerwa / Ma koszt).</summary>
public class Provision : CompanyEntity
{
    public ProvisionKind Kind { get; set; }
    public string Description { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public decimal Used { get; set; }
    public decimal Released { get; set; }
    public string AccountNumber { get; set; } = "";
    public string CostAccountNumber { get; set; } = "";
    public Guid? JournalEntryId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public decimal Remaining => Amount - Used - Released;
}
