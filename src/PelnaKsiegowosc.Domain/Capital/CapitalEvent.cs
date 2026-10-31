using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Capital;

/// <summary>
/// Wspólnik, akcjonariusz, członek, fundator lub właściciel (zależnie od formy prawnej jednostki).
/// Moduł nie zastępuje księgi udziałów, rejestru akcjonariuszy ani dokumentów korporacyjnych —
/// służy wyłącznie do ewidencji księgowej zdarzeń kapitałowych.
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
    /// <summary>Świadczenie pracy lub usług (np. w P.S.A. — art. 300^2 § 2 KSH, w spółce jawnej — art. 48 § 2 KSH) — nie jest wnoszone na kapitał podstawowy spółek kapitałowych; ujęcie księgowe wymaga oceny.</summary>
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
    /// <summary>Potwierdzenie dopuszczalności wypłaty na rzecz wspólników/akcjonariuszy (w P.S.A. — test wypłacalności, art. 300^15 KSH; w innych formach — ograniczenia właściwe dla formy, zob. <see cref="Companies.LegalForms.PayoutRule"/>).</summary>
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
    public IReadOnlyList<string> MissingDocuments(Companies.LegalForm form)
    {
        var missing = new List<string>();
        switch (Type)
        {
            case CapitalEventType.ObjecieAkcjiWkladPieniezny:
            case CapitalEventType.ObjecieAkcjiWkladNiepieniezny:
                if (string.IsNullOrWhiteSpace(AgreementReference)) missing.Add("umowa spółki / oświadczenie o objęciu akcji lub udziałów");
                if (Companies.LegalForms.SharesName(form) is { } shares && ShareCount is null or <= 0) missing.Add($"liczba obejmowanych tytułów ({shares})");
                if (ShareholderId is null) missing.Add(Companies.LegalForms.OwnerName(form));
                if (Type == CapitalEventType.ObjecieAkcjiWkladNiepieniezny && string.IsNullOrWhiteSpace(Description))
                    missing.Add("opis i wycena wkładu niepieniężnego");
                break;
            case CapitalEventType.PodwyzszenieKapitaluAkcyjnego:
            case CapitalEventType.ObnizenieKapitaluAkcyjnego:
                if (string.IsNullOrWhiteSpace(ResolutionReference)) missing.Add("uchwała organu właścicielskiego (zgromadzenia wspólników / walnego zgromadzenia) lub zgoda wspólników");
                if (Companies.LegalForms.RequiresKrs(form) && string.IsNullOrWhiteSpace(KrsEntryReference)) missing.Add("wpis zmiany do KRS (sygnatura)");
                break;
            case CapitalEventType.PodzialZysku:
            case CapitalEventType.PokrycieStraty:
            case CapitalEventType.UchwalenieDywidendy:
            case CapitalEventType.UtworzenieKapitaluRezerwowego:
            case CapitalEventType.PrzeniesienieNaKapitalZapasowy:
                if (string.IsNullOrWhiteSpace(ResolutionReference)) missing.Add("uchwała organu właścicielskiego (zgromadzenia wspólników / walnego zgromadzenia) lub zgoda wspólników");
                break;
            case CapitalEventType.ZaliczkaNaPoczetDywidendy:
                if (string.IsNullOrWhiteSpace(ResolutionReference)) missing.Add("uchwała organu zarządzającego oraz upoważnienie w umowie spółki/statucie");
                break;
            case CapitalEventType.PozyczkaOdAkcjonariusza:
                if (string.IsNullOrWhiteSpace(AgreementReference)) missing.Add("umowa pożyczki");
                if (ShareholderId is null) missing.Add(Companies.LegalForms.OwnerName(form));
                break;
        }
        var needsPayoutCheck = IsPayoutToShareholders(Type) ||
            form == Companies.LegalForm.ProstaSpolkaAkcyjna && Type == CapitalEventType.UchwalenieDywidendy;
        if (needsPayoutCheck && !SolvencyTestConfirmed)
            missing.Add("potwierdzenie dopuszczalności wypłaty: " + Companies.LegalForms.PayoutRule(form));
        if (needsPayoutCheck && form == Companies.LegalForm.ProstaSpolkaAkcyjna &&
            (string.IsNullOrWhiteSpace(SolvencyTestNotes) || SolvencyTestNotes.Trim().Length < 20))
            missing.Add("P.S.A.: udokumentowane wyliczenie dopuszczalnej kwoty podziału, wymaganego kapitału i prognozy płynności na 6 miesięcy (odnośnik do wyliczenia w polu oceny wypłacalności, min. 20 znaków)");
        if (TotalAmount <= 0 && ShareCapitalAmount <= 0) missing.Add("kwota zdarzenia");
        return missing;
    }
}
