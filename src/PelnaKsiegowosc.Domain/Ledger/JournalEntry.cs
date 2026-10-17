using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Ledger;

public enum EntryStatus
{
    /// <summary>Bufor — zapis roboczy, edytowalny, nie stanowi zapisu w księgach.</summary>
    Bufor,
    /// <summary>Zaksięgowany — zapis trwały (art. 23 ust. 3, art. 24 UoR), niemodyfikowalny.</summary>
    Zaksiegowany
}

public enum EntryKind
{
    Zwykly,
    /// <summary>Bilans otwarcia (BO).</summary>
    BilansOtwarcia,
    /// <summary>Storno (korekta) — zapis ujemny lub odwrotny do zapisu pierwotnego.</summary>
    Storno,
    /// <summary>Zapis korygujący (nowa, poprawna treść po stornie).</summary>
    Korekta,
    /// <summary>Zapis zamknięcia roku (przeksięgowanie wyniku).</summary>
    Zamkniecie
}

/// <summary>Dziennik częściowy (art. 14 ust. 3 UoR), np. SPR, ZAK, BANK, KASA, PK.</summary>
public class JournalDefinition : CompanyEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Zapis księgowy (dekret) w dzienniku. Zawiera elementy wymagane art. 23 ust. 2 UoR:
/// datę operacji, rodzaj i numer dowodu (oraz jego datę), tekst operacji, kwotę i datę zapisu,
/// oznaczenie kont; oraz automatycznie nadany numer pozycji w dzienniku i identyfikator osoby
/// odpowiedzialnej (art. 23 ust. 3 UoR).
/// </summary>
public class JournalEntry : CompanyEntity
{
    public Guid FiscalYearId { get; set; }
    public Guid PeriodId { get; set; }

    /// <summary>Kolejny numer pozycji w dzienniku w roku obrotowym (nadawany przy zaksięgowaniu, ciągły, bez luk).</summary>
    public long? SequenceNumber { get; set; }
    /// <summary>Numer w dzienniku częściowym, np. „SPR/2026/000012”.</summary>
    public string? JournalNumber { get; set; }
    public string JournalCode { get; set; } = "PK";

    public EntryStatus Status { get; set; } = EntryStatus.Bufor;
    public EntryKind Kind { get; set; } = EntryKind.Zwykly;

    public DateOnly OperationDate { get; set; }
    public DateOnly DocumentDate { get; set; }
    public DateOnly PostingDate { get; set; }

    public string Description { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
    public Guid? SourceDocumentId { get; set; }

    public string Currency { get; set; } = "PLN";
    public decimal? ExchangeRate { get; set; }
    public string? ExchangeRateTable { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid? PostedByUserId { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public Guid? ModifiedByUserId { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }

    /// <summary>Zapis, który jest stornowany przez ten zapis.</summary>
    public Guid? ReversesEntryId { get; set; }
    /// <summary>Zapis, którego treść ten zapis koryguje (zapis poprawny po stornie).</summary>
    public Guid? CorrectsEntryId { get; set; }
    /// <summary>Zapis stornujący ten zapis (jeżeli został wystornowany).</summary>
    public Guid? ReversedByEntryId { get; set; }
    public string? CorrectionReason { get; set; }

    /// <summary>Skrót SHA-256 poprzedniego zaksięgowanego zapisu w roku (łańcuch integralności).</summary>
    public string? PreviousHash { get; set; }
    /// <summary>Skrót SHA-256 treści zapisu i <see cref="PreviousHash"/>.</summary>
    public string? Hash { get; set; }

    public List<JournalLine> Lines { get; set; } = new();

    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);
    public bool IsPosted => Status == EntryStatus.Zaksiegowany;

    /// <summary>
    /// Kanoniczna reprezentacja treści zapisu (do skrótu). Zmiana dowolnego pola księgowego
    /// zaksięgowanego zapisu zmienia skrót i jest wykrywana przez kontrolę integralności.
    /// </summary>
    public string CanonicalContent()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("v1|").Append(Id).Append('|').Append(CompanyId).Append('|').Append(FiscalYearId).Append('|')
          .Append(SequenceNumber?.ToString(inv)).Append('|').Append(JournalNumber).Append('|').Append(JournalCode).Append('|')
          .Append(Kind).Append('|')
          .Append(OperationDate.ToString("yyyy-MM-dd", inv)).Append('|')
          .Append(DocumentDate.ToString("yyyy-MM-dd", inv)).Append('|')
          .Append(PostingDate.ToString("yyyy-MM-dd", inv)).Append('|')
          .Append(Description).Append('|').Append(DocumentType).Append('|').Append(DocumentNumber).Append('|')
          .Append(SourceDocumentId).Append('|').Append(Currency).Append('|').Append(ExchangeRate?.ToString("0.######", inv)).Append('|')
          .Append(PostedByUserId).Append('|').Append(PostedAtUtc?.ToString("O", inv)).Append('|')
          .Append(ReversesEntryId).Append('|').Append(CorrectsEntryId).Append('|');
        foreach (var l in Lines.OrderBy(l => l.LineNo))
        {
            sb.Append('#').Append(l.LineNo.ToString(inv)).Append(';').Append(l.AccountId).Append(';').Append(l.AccountNumber).Append(';')
              .Append(Money.Xml(l.Debit)).Append(';').Append(Money.Xml(l.Credit)).Append(';')
              .Append(l.CurrencyAmount?.ToString("0.00", inv)).Append(';').Append(l.CounterpartyId).Append(';')
              .Append(l.OpenItemId).Append(';').Append(l.Description);
        }
        return sb.ToString();
    }

    public string ComputeHash()
    {
        var bytes = Encoding.UTF8.GetBytes((PreviousHash ?? "GENESIS") + "\n" + CanonicalContent());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

/// <summary>Pozycja dekretu — kwota po stronie Wn albo Ma (nigdy obie).</summary>
public class JournalLine : Entity
{
    public Guid EntryId { get; set; }
    public int LineNo { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>Numer konta utrwalony w chwili księgowania (czytelność wydruku i skrótu).</summary>
    public string AccountNumber { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal? CurrencyAmount { get; set; }
    public Guid? CounterpartyId { get; set; }
    /// <summary>Pozycja rozrachunkowa (należność/zobowiązanie), której dotyczy zapis.</summary>
    public Guid? OpenItemId { get; set; }
    public string? Description { get; set; }

    public decimal Amount => Debit != 0 ? Debit : Credit;
    public bool IsDebit => Debit != 0;
}
