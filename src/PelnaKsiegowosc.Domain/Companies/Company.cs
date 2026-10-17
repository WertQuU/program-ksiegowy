using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Domain.Companies;

public enum LegalForm
{
    /// <summary>Prosta spółka akcyjna (art. 300^1 i n. KSH).</summary>
    ProstaSpolkaAkcyjna
}

public enum VatStatus
{
    CzynnyPodatnikVat,
    ZwolnionyPodmiotowo,
    NiezarejestrowanyVat
}

public enum VatSettlementPeriod
{
    Miesieczny,
    Kwartalny
}

public enum CitAdvanceMethod
{
    /// <summary>Zaliczki miesięczne (art. 25 ust. 1 ustawy o CIT).</summary>
    Miesieczne,
    /// <summary>Zaliczki kwartalne — mały podatnik / rozpoczynający działalność (art. 25 ust. 1b).</summary>
    Kwartalne
}

public enum ProfitAndLossVariant
{
    /// <summary>Wariant porównawczy (załącznik nr 1 do UoR).</summary>
    Porownawczy,
    /// <summary>Wariant kalkulacyjny — niezaimplementowany w raportach (oznaczony w aplikacji).</summary>
    Kalkulacyjny
}

/// <summary>Sposób korygowania zaksięgowanych zapisów (art. 25 ust. 1 pkt 2 UoR — wyłącznie zapisami dodatnimi lub ujemnymi przy księgach komputerowych).</summary>
public enum CorrectionMethod
{
    /// <summary>Storno czerwone — zapis ujemny po tej samej stronie (nie zawyża obrotów).</summary>
    StornoCzerwone,
    /// <summary>Storno czarne — zapis odwrotny po przeciwnych stronach.</summary>
    StornoCzarne
}

/// <summary>Jednostka (spółka). Aplikacja obsługuje obecnie jedną P.S.A.; model jest przygotowany na wiele jednostek.</summary>
public class Company : Entity
{
    public string FullName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public LegalForm LegalForm { get; set; } = LegalForm.ProstaSpolkaAkcyjna;
    public string Nip { get; set; } = "";
    public string? Regon { get; set; }
    public string? Krs { get; set; }
    public string? RegistryCourt { get; set; }

    public string Street { get; set; } = "";
    public string BuildingNumber { get; set; } = "";
    public string? ApartmentNumber { get; set; }
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "PL";
    public string? Voivodeship { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>Kod urzędu skarbowego (4 cyfry) — wymagany w nagłówku JPK.</summary>
    public string? TaxOfficeCode { get; set; }

    public string FunctionalCurrency { get; set; } = "PLN";
    public VatStatus VatStatus { get; set; } = VatStatus.CzynnyPodatnikVat;
    public VatSettlementPeriod VatPeriod { get; set; } = VatSettlementPeriod.Miesieczny;
    public CitAdvanceMethod CitAdvanceMethod { get; set; } = CitAdvanceMethod.Miesieczne;

    /// <summary>Status małego podatnika CIT (art. 4a pkt 10) — deklarowany przez użytkownika na dany rok.</summary>
    public bool IsSmallCitTaxpayer { get; set; }

    /// <summary>Data rejestracji w KRS (dla P.S.A. ma znaczenie m.in. dla terminu wniesienia wkładów).</summary>
    public DateOnly? RegistrationDate { get; set; }

    /// <summary>Konto bankowe do dokumentów (IBAN).</summary>
    public string? DefaultBankAccountIban { get; set; }
    public string? InvoiceFooter { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ValidationResult Validate()
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(FullName)) r.Error("Pełna nazwa spółki jest wymagana.");
        else if (!FullName.Contains("prosta spółka akcyjna", StringComparison.OrdinalIgnoreCase) &&
                 !FullName.Contains("P.S.A.", StringComparison.OrdinalIgnoreCase))
            r.Warning("Firma P.S.A. powinna zawierać oznaczenie „prosta spółka akcyjna” lub skrót „P.S.A.” (art. 300^7 KSH — do weryfikacji prawnej).");
        if (!Identifiers.IsValidNip(Nip)) r.Error("NIP spółki jest nieprawidłowy (błędna suma kontrolna lub format).");
        if (!string.IsNullOrWhiteSpace(Regon) && !Identifiers.IsValidRegon(Regon)) r.Error("REGON jest nieprawidłowy.");
        if (!string.IsNullOrWhiteSpace(Krs) && !Identifiers.IsValidKrs(Krs)) r.Error("Numer KRS musi składać się z 10 cyfr.");
        if (string.IsNullOrWhiteSpace(Krs)) r.Warning("Brak numeru KRS — P.S.A. podlega wpisowi do rejestru przedsiębiorców KRS.");
        if (string.IsNullOrWhiteSpace(City) || string.IsNullOrWhiteSpace(Street)) r.Error("Adres siedziby jest niekompletny.");
        if (Country == "PL" && !Identifiers.IsValidPostalCode(PostalCode)) r.Error("Kod pocztowy musi mieć format 00-000.");
        if (!string.IsNullOrWhiteSpace(TaxOfficeCode) && (TaxOfficeCode.Length != 4 || !TaxOfficeCode.All(char.IsDigit)))
            r.Error("Kod urzędu skarbowego musi mieć 4 cyfry.");
        if (string.IsNullOrWhiteSpace(TaxOfficeCode)) r.Warning("Brak kodu urzędu skarbowego — wymagany do plików JPK.");
        if (!string.IsNullOrWhiteSpace(DefaultBankAccountIban) && !Identifiers.IsValidIban(DefaultBankAccountIban))
            r.Error("Numer rachunku bankowego (IBAN/NRB) jest nieprawidłowy.");
        if (FunctionalCurrency != "PLN") r.Error("Księgi rachunkowe prowadzi się w walucie polskiej (art. 9 ust. 1 UoR).");
        return r;
    }

    public string AddressLine1 => $"{Street} {BuildingNumber}{(string.IsNullOrWhiteSpace(ApartmentNumber) ? "" : "/" + ApartmentNumber)}";
    public string AddressLine2 => $"{PostalCode} {City}";
}

public enum ResponsibleRole
{
    CzlonekZarzadu,
    PrzewodniczacyRadyDyrektorow,
    DyrektorWykonawczy,
    GlownyKsiegowy,
    OsobaProwadzacaKsiegi,
    BiuroRachunkowe,
    Pelnomocnik
}

/// <summary>Osoby odpowiedzialne — kierownik jednostki (art. 3 ust. 1 pkt 6 UoR), osoba prowadząca księgi, osoba sporządzająca sprawozdanie.</summary>
public class ResponsiblePerson : CompanyEntity
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public ResponsibleRole Role { get; set; }
    public string? Email { get; set; }
    public string? Pesel { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public bool SignsFinancialStatements { get; set; }
    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>
/// Polityka rachunkowości (art. 10 UoR). Aplikacja przechowuje parametry wpływające na księgowanie
/// oraz treść dokumentu przyjętego przez kierownika jednostki. Szablon jest punktem wyjścia,
/// który MUSI zostać dostosowany i zatwierdzony przez kierownika jednostki.
/// </summary>
public class AccountingPolicy : CompanyEntity
{
    public int Version { get; set; } = 1;
    public DateOnly EffectiveFrom { get; set; }
    public string Title { get; set; } = "Zasady (polityka) rachunkowości";

    /// <summary>Pełna treść dokumentu (Markdown).</summary>
    public string Content { get; set; } = "";

    public ProfitAndLossVariant ProfitAndLossVariant { get; set; } = ProfitAndLossVariant.Porownawczy;
    public CorrectionMethod CorrectionMethod { get; set; } = CorrectionMethod.StornoCzerwone;

    /// <summary>Czy koszty ewidencjonuje się w układzie rodzajowym (zespół 4).</summary>
    public bool CostsByNature { get; set; } = true;
    /// <summary>Czy prowadzony jest również układ funkcjonalny (zespół 5).</summary>
    public bool CostsByFunction { get; set; }

    /// <summary>Próg istotności (kwotowo) używany w ostrzeżeniach.</summary>
    public decimal MaterialityThreshold { get; set; }

    /// <summary>Jednorazowy odpis środków o niskiej wartości — próg (np. 10 000 zł).</summary>
    public decimal LowValueAssetThreshold { get; set; } = 10000m;

    /// <summary>Czy stosuje się podatkowe stawki amortyzacji do celów bilansowych (art. 32 ust. 6 UoR).</summary>
    public bool UseTaxDepreciationRatesForBooks { get; set; } = true;

    /// <summary>Czy jednostka stosuje uproszczenia dla jednostek małych (art. 3 ust. 1c UoR).</summary>
    public bool IsSmallEntity { get; set; }
    public bool IsMicroEntity { get; set; }

    public bool ApprovedByManagement { get; set; }
    public DateOnly? ApprovedOn { get; set; }
    public string? ApprovedBy { get; set; }

    public ValidationResult Validate()
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(Content) || Content.Length < 200)
            r.Error("Treść polityki rachunkowości jest pusta lub zbyt krótka — wymagana dokumentacja zasad (art. 10 ust. 1 UoR).");
        if (!ApprovedByManagement)
            r.Error("Polityka rachunkowości nie została zatwierdzona przez kierownika jednostki (art. 10 ust. 2 UoR).");
        if (Content.Contains("[DO UZUPEŁNIENIA]", StringComparison.OrdinalIgnoreCase))
            r.Error("Polityka rachunkowości zawiera niewypełnione pola oznaczone [DO UZUPEŁNIENIA].");
        if (ProfitAndLossVariant == ProfitAndLossVariant.Kalkulacyjny)
            r.Warning("Wariant kalkulacyjny RZiS nie jest jeszcze obsługiwany przez raporty aplikacji.");
        if (IsMicroEntity && IsSmallEntity) r.Error("Jednostka nie może być jednocześnie mikro i małą — wybierz jeden wariant uproszczeń.");
        return r;
    }
}
