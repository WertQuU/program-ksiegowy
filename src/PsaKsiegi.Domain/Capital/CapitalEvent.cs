using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Domain.Capital;

/// <summary>
/// Moduł nie zastępuje rejestru akcjonariuszy prowadzonego przez podmiot uprawniony (art. 300^31 KSH)
/// ani dokumentów korporacyjnych. Służy wyłącznie do ewidencji księgowej zdarzeń kapitałowych PSA.
/// </summary>
public class Shareholder : CompanyEntity
{
    public string Name { get; set; } = "";
    public Guid? CounterpartyId { get; set; }
    public string? Identifier { get; set; }
    public bool IsLegalPerson { get; set; }
    public string? Notes { get; set; }
}

public enum ContributionType
{
    Pieniezny,
    Niepieniezny,
    /// <summary>Świadczenie pracy lub usług (art. 300^2 § 2 KSH) — nie stanowi wkładu na kapitał akcyjny; ujęcie księgowe wymaga oceny.</summary>
    PracaLubUslugi
}

public enum CapitalEventType
{
    ObjecieAkcjiWkladPieniezny,
    ObjecieAkcjiWkladNiepieniezny,
    WplataWkladu,
    PodwyzszenieKapitaluAkcyjnego,
    ObnizenieKapitaluAkcyjnego,
    PrzeniesienieNaKapitalZapasowy,
    UtworzenieKapitaluRezerwowego,
    PodzialZysku,
    PokrycieStraty,
    UchwalenieDywidendy,
    WyplataDywidendy,
    ZaliczkaNaPoczetDywidendy,
    PozyczkaOdAkcjonariusza,
    SplataPozyczkiAkcjonariusza,
    Inne
}

public enum CapitalEventStatus
{
    Roboczy,
    /// <summary>Dokumenty korporacyjne potwierdzone przez uprawnionego użytkownika.</summary>
    DokumentyPotwierdzone,
    Zaksiegowany,
    Anulowany
}

/// <summary>Zdarzenie kapitałowe. Księgowanie możliwe dopiero po potwierdzeniu wymaganych dokumentów (uchwała, umowa, wpis do KRS itp.).</summary>
public class CapitalEvent : CompanyEntity
{
    public CapitalEventType Type { get; set; }
    public CapitalEventStatus Status { get; set; } = CapitalEventStatus.Roboczy;
    public DateOnly EventDate { get; set; }
    public Guid? ShareholderId { get; set; }
    public string? ShareSeries { get; set; }
    public long? ShareCount { get; set; }
    public ContributionType? ContributionType { get; set; }
    /// <summary>Kwota przypisana do kapitału akcyjnego.</summary>
    public decimal ShareCapitalAmount { get; set; }
    /// <summary>Kwota przekazywana na kapitał zapasowy (nadwyżka wkładu).</summary>
    public decimal SupplementaryCapitalAmount { get; set; }
    /// <summary>Kwota ogółem zdarzenia (np. wypłata, pożyczka).</summary>
    public decimal TotalAmount { get; set; }
    /// <summary>Podatek pobierany przez płatnika (np. zryczałtowany podatek od dywidendy).</summary>
    public decimal WithholdingTaxAmount { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Konto Wn wskazane przez użytkownika (np. składnik wniesiony aportem, kapitał, z którego następuje przeksięgowanie).</summary>
    public string? DebitAccountNumber { get; set; }
    /// <summary>Konto Ma wskazane przez użytkownika (np. przy obniżeniu kapitału).</summary>
    public string? CreditAccountNumber { get; set; }

    public string? ResolutionReference { get; set; }
    public DateOnly? ResolutionDate { get; set; }
    public string? AgreementReference { get; set; }
    public string? KrsEntryReference { get; set; }
    /// <summary>Potwierdzenie przeprowadzenia testu wypłacalności przed wypłatą na rzecz akcjonariuszy (art. 300^15 § 3 KSH — do weryfikacji prawnej).</summary>
    public bool SolvencyTestConfirmed { get; set; }
    public string? SolvencyTestNotes { get; set; }

    public Guid? ConfirmedByUserId { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public static bool IsPayoutToShareholders(CapitalEventType t) =>
        t is CapitalEventType.WyplataDywidendy or CapitalEventType.ZaliczkaNaPoczetDywidendy or CapitalEventType.ObnizenieKapitaluAkcyjnego;

    /// <summary>Lista dokumentów, które muszą zostać wskazane przed potwierdzeniem zdarzenia.</summary>
    public IReadOnlyList<string> MissingDocuments()
    {
        var missing = new List<string>();
        switch (Type)
        {
            case CapitalEventType.ObjecieAkcjiWkladPieniezny:
            case CapitalEventType.ObjecieAkcjiWkladNiepieniezny:
                if (string.IsNullOrWhiteSpace(AgreementReference)) missing.Add("umowa spółki / oświadczenie o objęciu akcji");
                if (ShareCount is null or <= 0) missing.Add("liczba obejmowanych akcji");
                if (ShareholderId is null) missing.Add("akcjonariusz");
                if (Type == CapitalEventType.ObjecieAkcjiWkladNiepieniezny && string.IsNullOrWhiteSpace(Description))
                    missing.Add("opis i wycena wkładu niepieniężnego");
                break;
            case CapitalEventType.PodwyzszenieKapitaluAkcyjnego:
            case CapitalEventType.ObnizenieKapitaluAkcyjnego:
                if (string.IsNullOrWhiteSpace(ResolutionReference)) missing.Add("uchwała walnego zgromadzenia");
                if (string.IsNullOrWhiteSpace(KrsEntryReference)) missing.Add("wpis zmiany do KRS (sygnatura)");
                break;
            case CapitalEventType.PodzialZysku:
            case CapitalEventType.PokrycieStraty:
            case CapitalEventType.UchwalenieDywidendy:
            case CapitalEventType.UtworzenieKapitaluRezerwowego:
            case CapitalEventType.PrzeniesienieNaKapitalZapasowy:
                if (string.IsNullOrWhiteSpace(ResolutionReference)) missing.Add("uchwała walnego zgromadzenia");
                break;
            case CapitalEventType.ZaliczkaNaPoczetDywidendy:
                if (string.IsNullOrWhiteSpace(ResolutionReference)) missing.Add("uchwała zarządu/rady dyrektorów oraz upoważnienie w umowie spółki");
                break;
            case CapitalEventType.PozyczkaOdAkcjonariusza:
                if (string.IsNullOrWhiteSpace(AgreementReference)) missing.Add("umowa pożyczki");
                if (ShareholderId is null) missing.Add("akcjonariusz");
                break;
        }
        if (IsPayoutToShareholders(Type) && !SolvencyTestConfirmed)
            missing.Add("potwierdzenie testu wypłacalności (wypłata nie może prowadzić do utraty zdolności do wykonywania wymagalnych zobowiązań)");
        if (TotalAmount <= 0 && ShareCapitalAmount <= 0) missing.Add("kwota zdarzenia");
        return missing;
    }
}
