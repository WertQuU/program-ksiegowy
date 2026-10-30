using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Domain.Companies;

/// <summary>Forma prawna jednostki prowadzącej księgi rachunkowe (art. 2 ust. 1 UoR).</summary>
public enum LegalForm
{
    SpolkaZOgraniczonaOdpowiedzialnoscia,
    ProstaSpolkaAkcyjna,
    SpolkaAkcyjna,
    SpolkaKomandytowoAkcyjna,
    SpolkaKomandytowa,
    SpolkaJawna,
    SpolkaPartnerska,
    SpolkaCywilna,
    /// <summary>Osoba fizyczna prowadząca działalność gospodarczą — księgi rachunkowe (art. 2 ust. 1 pkt 2 UoR).</summary>
    DzialalnoscGospodarczaOsobyFizycznej,
    Fundacja,
    Stowarzyszenie,
    Spoldzielnia,
    Inna
}

/// <summary>Cechy formy prawnej wykorzystywane w walidacji, planie kont, module kapitałów i podatku dochodowym.</summary>
public static class LegalForms
{
    public static string Name(LegalForm f) => f switch
    {
        LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia => "spółka z ograniczoną odpowiedzialnością",
        LegalForm.ProstaSpolkaAkcyjna => "prosta spółka akcyjna",
        LegalForm.SpolkaAkcyjna => "spółka akcyjna",
        LegalForm.SpolkaKomandytowoAkcyjna => "spółka komandytowo-akcyjna",
        LegalForm.SpolkaKomandytowa => "spółka komandytowa",
        LegalForm.SpolkaJawna => "spółka jawna",
        LegalForm.SpolkaPartnerska => "spółka partnerska",
        LegalForm.SpolkaCywilna => "spółka cywilna",
        LegalForm.DzialalnoscGospodarczaOsobyFizycznej => "działalność gospodarcza osoby fizycznej",
        LegalForm.Fundacja => "fundacja",
        LegalForm.Stowarzyszenie => "stowarzyszenie",
        LegalForm.Spoldzielnia => "spółdzielnia",
        _ => "inna jednostka"
    };

    /// <summary>Oznaczenia formy prawnej w firmie (nazwie) spółki handlowej — pełne i skrócone.</summary>
    public static IReadOnlyList<string> Designations(LegalForm f) => f switch
    {
        LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia => new[] { "spółka z ograniczoną odpowiedzialnością", "sp. z o.o.", "spółka z o.o." },
        LegalForm.ProstaSpolkaAkcyjna => new[] { "prosta spółka akcyjna", "P.S.A." },
        LegalForm.SpolkaAkcyjna => new[] { "spółka akcyjna", "S.A." },
        LegalForm.SpolkaKomandytowoAkcyjna => new[] { "spółka komandytowo-akcyjna", "S.K.A." },
        LegalForm.SpolkaKomandytowa => new[] { "spółka komandytowa", "sp.k." },
        LegalForm.SpolkaJawna => new[] { "spółka jawna", "sp.j." },
        LegalForm.SpolkaPartnerska => new[] { "spółka partnerska", "sp.p.", "i partner", "i partnerzy" },
        _ => Array.Empty<string>()
    };

    /// <summary>Czy jednostka podlega wpisowi do rejestru przedsiębiorców lub stowarzyszeń KRS.</summary>
    public static bool RequiresKrs(LegalForm f) => f is not (LegalForm.SpolkaCywilna or LegalForm.DzialalnoscGospodarczaOsobyFizycznej or LegalForm.Inna);

    /// <summary>
    /// Czy jednostka jest podatnikiem CIT (art. 1 ustawy o CIT). Spółki jawne, partnerskie, cywilne i osoby fizyczne
    /// nie są — podatek dochodowy rozliczają wspólnicy/właściciel (PIT). Spółka jawna może w szczególnych przypadkach
    /// stać się podatnikiem CIT (art. 1 ust. 3 pkt 1a) — wymaga oceny i nie jest obsługiwane automatycznie.
    /// </summary>
    public static bool IsCitTaxpayer(LegalForm f) => f is not (LegalForm.SpolkaJawna or LegalForm.SpolkaPartnerska or LegalForm.SpolkaCywilna or LegalForm.DzialalnoscGospodarczaOsobyFizycznej);

    /// <summary>Nazwa kapitału (funduszu) podstawowego — konto 801 i pozycja A.I pasywów.</summary>
    public static string ShareCapitalName(LegalForm f) => f switch
    {
        LegalForm.ProstaSpolkaAkcyjna => "Kapitał akcyjny",
        LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia or LegalForm.SpolkaAkcyjna or LegalForm.SpolkaKomandytowoAkcyjna => "Kapitał zakładowy",
        LegalForm.Spoldzielnia => "Fundusz udziałowy",
        LegalForm.Fundacja => "Fundusz założycielski",
        LegalForm.Stowarzyszenie => "Fundusz statutowy",
        LegalForm.DzialalnoscGospodarczaOsobyFizycznej => "Kapitał właściciela",
        LegalForm.Inna => "Kapitał (fundusz) podstawowy",
        _ => "Kapitał wspólników"
    };

    /// <summary>Określenie osoby posiadającej udział w kapitale (akcjonariusz, wspólnik, członek, fundator, właściciel).</summary>
    public static string OwnerName(LegalForm f) => f switch
    {
        LegalForm.ProstaSpolkaAkcyjna or LegalForm.SpolkaAkcyjna => "akcjonariusz",
        LegalForm.SpolkaKomandytowoAkcyjna => "akcjonariusz / komplementariusz",
        LegalForm.Spoldzielnia => "członek",
        LegalForm.Fundacja => "fundator",
        LegalForm.Stowarzyszenie => "członek",
        LegalForm.DzialalnoscGospodarczaOsobyFizycznej => "właściciel",
        _ => "wspólnik"
    };

    /// <summary>Jednostka tytułów uczestnictwa w kapitale (akcje, udziały) — null, gdy forma ich nie przewiduje.</summary>
    public static string? SharesName(LegalForm f) => f switch
    {
        LegalForm.ProstaSpolkaAkcyjna or LegalForm.SpolkaAkcyjna or LegalForm.SpolkaKomandytowoAkcyjna => "akcje",
        LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia or LegalForm.Spoldzielnia => "udziały",
        _ => null
    };

    /// <summary>Organ będący kierownikiem jednostki (art. 3 ust. 1 pkt 6 UoR).</summary>
    public static string ManagementBody(LegalForm f) => f switch
    {
        LegalForm.ProstaSpolkaAkcyjna => "zarząd albo rada dyrektorów",
        LegalForm.SpolkaJawna or LegalForm.SpolkaPartnerska or LegalForm.SpolkaCywilna => "wspólnicy prowadzący sprawy spółki",
        LegalForm.SpolkaKomandytowa or LegalForm.SpolkaKomandytowoAkcyjna => "komplementariusze prowadzący sprawy spółki",
        LegalForm.DzialalnoscGospodarczaOsobyFizycznej => "osoba fizyczna prowadząca działalność",
        _ => "zarząd"
    };

    /// <summary>Podstawa prawna ograniczeń wypłat na rzecz wspólników/akcjonariuszy — do opisu wymaganych potwierdzeń.</summary>
    public static string PayoutRule(LegalForm f) => f switch
    {
        LegalForm.ProstaSpolkaAkcyjna => "test wypłacalności (art. 300^15 KSH) — wypłata nie może prowadzić do utraty zdolności do wykonywania wymagalnych zobowiązań",
        LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia => "ograniczenia wypłat z art. 191–195 KSH (m.in. limit kwoty przeznaczonej do podziału)",
        LegalForm.SpolkaAkcyjna or LegalForm.SpolkaKomandytowoAkcyjna => "ograniczenia wypłat z art. 344 i 347–349 KSH",
        _ => "zgodność wypłaty z umową/statutem i przepisami właściwymi dla formy prawnej"
    };
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

/// <summary>Jednostka prowadząca księgi rachunkowe. Interfejs obsługuje obecnie jedną jednostkę w bazie; model jest przygotowany na wiele jednostek.</summary>
public class Company : Entity
{
    public string FullName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public LegalForm LegalForm { get; set; } = LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia;
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
    /// <summary>Powiat i gmina siedziby — wymagane w adresie JPK_KR_PD i e-Sprawozdaniu.</summary>
    public string? County { get; set; }
    public string? Commune { get; set; }
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

    /// <summary>Data rejestracji w KRS (lub rozpoczęcia działalności, gdy jednostka nie podlega wpisowi).</summary>
    public DateOnly? RegistrationDate { get; set; }

    /// <summary>Konto bankowe do dokumentów (IBAN).</summary>
    public string? DefaultBankAccountIban { get; set; }
    public string? InvoiceFooter { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ValidationResult Validate()
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(FullName)) r.Error("Pełna nazwa jednostki jest wymagana.");
        else if (LegalForms.Designations(LegalForm) is { Count: > 0 } designations &&
                 !designations.Any(d => FullName.Contains(d, StringComparison.OrdinalIgnoreCase)))
            r.Warning($"Firma spółki powinna zawierać oznaczenie formy prawnej („{designations[0]}” lub „{designations[1]}”) — do weryfikacji z umową spółki i KRS.");
        if (!Identifiers.IsValidNip(Nip)) r.Error("NIP jest nieprawidłowy (błędna suma kontrolna lub format).");
        if (!string.IsNullOrWhiteSpace(Regon) && !Identifiers.IsValidRegon(Regon)) r.Error("REGON jest nieprawidłowy.");
        if (!string.IsNullOrWhiteSpace(Krs) && !Identifiers.IsValidKrs(Krs)) r.Error("Numer KRS musi składać się z 10 cyfr.");
        if (string.IsNullOrWhiteSpace(Krs) && LegalForms.RequiresKrs(LegalForm))
            r.Warning($"Brak numeru KRS — {LegalForms.Name(LegalForm)} podlega wpisowi do Krajowego Rejestru Sądowego.");
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
        if (Content.Contains("[DO UZUPEŁNIENIA", StringComparison.OrdinalIgnoreCase))
            r.Error("Polityka rachunkowości zawiera niewypełnione pola oznaczone [DO UZUPEŁNIENIA].");
        if (ProfitAndLossVariant == ProfitAndLossVariant.Kalkulacyjny && !CostsByFunction)
            r.Warning("Wariant kalkulacyjny RZiS bez kont zespołu 5: koszty rodzajowe są dzielone kluczem podziału (Raporty → Klucz podziału kosztów) — opisz klucz w polityce rachunkowości.");
        if (IsMicroEntity && IsSmallEntity) r.Error("Jednostka nie może być jednocześnie mikro i małą — wybierz jeden wariant uproszczeń.");
        return r;
    }
}
