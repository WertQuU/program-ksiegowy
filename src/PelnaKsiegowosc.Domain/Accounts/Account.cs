using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Accounts;

/// <summary>Typ konta — determinuje sposób prezentacji salda.</summary>
public enum AccountType
{
    /// <summary>Konto aktywów (saldo Wn).</summary>
    Aktywne,
    /// <summary>Konto pasywów (saldo Ma).</summary>
    Pasywne,
    /// <summary>Konto aktywno-pasywne (rozrachunkowe) — saldo dwustronne, prezentowane rozłącznie (zakaz kompensat — art. 7 ust. 3 UoR).</summary>
    AktywnoPasywne,
    /// <summary>Konto wynikowe kosztowe (zespoły 4, 5, część 7).</summary>
    WynikoweKosztowe,
    /// <summary>Konto wynikowe przychodowe (zespół 7).</summary>
    WynikowePrzychodowe,
    /// <summary>Konto wyniku finansowego (860).</summary>
    WynikFinansowy,
    /// <summary>Konto pozabilansowe — nie podlega zasadzie podwójnego zapisu.</summary>
    Pozabilansowe
}

/// <summary>Zespół kont wzorcowego planu kont.</summary>
public enum AccountGroup
{
    Z0_AktywaTrwale = 0,
    Z1_SrodkiPieniezne = 1,
    Z2_Rozrachunki = 2,
    Z3_Materialy = 3,
    Z4_KosztyRodzajowe = 4,
    Z5_KosztyFunkcjonalne = 5,
    Z6_Produkty = 6,
    Z7_PrzychodyIKoszty = 7,
    Z8_KapitalyIWynik = 8,
    Z9_Pozabilansowe = 9
}

/// <summary>Domyślna klasyfikacja podatkowa (CIT) obrotów konta.</summary>
/// <summary>
/// Kategoria przepływów pieniężnych konta przeciwstawnego (RPP, zał. nr 1 do UoR) — gdy pusta, wynika z pozycji bilansu / RZiS konta.
/// Ustawia się ją dla kont, których pozycja bilansu nie rozstrzyga treści (np. leasing finansowy w „innych zobowiązaniach
/// finansowych”, udzielone pożyczki, rozrachunki z tytułu dywidend, nieruchomości inwestycyjne).
/// </summary>
public enum CashFlowCategory
{
    Operacyjna,
    RzeczoweAktywaTrwaleIWnip,
    InwestycjeWNieruchomosci,
    AktywaFinansowePowiazane,
    AktywaFinansowe,
    PozyczkiUdzielone,
    InneInwestycyjne,
    KapitalWlasny,
    WyplatyNaRzeczWlascicieli,
    InneWydatkiZPodzialuZysku,
    UdzialyWlasne,
    KredytyIPozyczki,
    DluznePapieryWartosciowe,
    InneZobowiazaniaFinansowe,
    LeasingFinansowy,
    InneFinansowe
}

/// <summary>Kwalifikacja do limitu kosztów finansowania dłużnego (art. 15c ust. 12 ustawy o CIT).</summary>
public enum DebtFinancingKind
{
    /// <summary>Nie dotyczy (wyłączenie z art. 15c — np. odsetki budżetowe, koszt niebędący finansowaniem dłużnym).</summary>
    NieDotyczy,
    /// <summary>Koszt finansowania dłużnego: odsetki, prowizje, opłaty, premie, część odsetkowa raty leasingu, różnice kursowe od finansowania itp.</summary>
    Koszt,
    /// <summary>Przychód o charakterze odsetkowym (pomniejsza koszty przy ustalaniu nadwyżki).</summary>
    PrzychodOdsetkowy
}

public enum CitClassification
{
    NieDotyczy,
    KosztPodatkowy,
    KosztNiepodatkowy,
    PrzychodPodatkowy,
    PrzychodNiepodatkowy,
    /// <summary>Różnica wymagająca analizy (np. rezerwy, odsetki naliczone, różnice kursowe niezrealizowane).</summary>
    DoAnalizy,
    /// <summary>Przychód z zysków kapitałowych (art. 7b ustawy o CIT) — odrębne źródło, zawsze stawka 19%.</summary>
    PrzychodZyskiKapitalowe,
    /// <summary>Koszt uzyskania przychodu z zysków kapitałowych (art. 7b) — odrębne źródło.</summary>
    KosztZyskiKapitalowe
}

/// <summary>Wersja planu kont — powiązana z rokiem obrotowym (nowa wersja przy otwarciu roku).</summary>
public class ChartOfAccounts : CompanyEntity
{
    public Guid FiscalYearId { get; set; }
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public bool IsApproved { get; set; }
    public DateOnly? ApprovedOn { get; set; }
    public Guid? CopiedFromId { get; set; }
}

/// <summary>
/// Konto księgi głównej (syntetyczne) lub pomocnicze (analityczne). Numer analityki: „syntetyka-analityka”, np. „201-0001”.
/// </summary>
public class Account : CompanyEntity
{
    public Guid ChartId { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public int Level { get; set; }
    public AccountType Type { get; set; }
    public AccountGroup Group { get; set; }

    /// <summary>Konto rozrachunkowe — każdy zapis wymaga wskazania kontrahenta i jest objęty ewidencją rozrachunków.</summary>
    public bool IsSettlementAccount { get; set; }
    /// <summary>Wymaga podania kontrahenta w pozycji dekretu.</summary>
    public bool RequiresCounterparty { get; set; }
    /// <summary>Konto walutowe (np. rachunek w EUR).</summary>
    public string? Currency { get; set; }

    /// <summary>Kod pozycji bilansu przy saldzie Wn (np. „A.II.1.c”), wg <c>FinancialStatementLayout</c>.</summary>
    public string? BalanceDebitLine { get; set; }
    /// <summary>Kod pozycji bilansu przy saldzie Ma (np. „P.B.III.3.d”).</summary>
    public string? BalanceCreditLine { get; set; }
    /// <summary>Kod pozycji RZiS (np. „RZiS.B.III”).</summary>
    public string? ProfitLossLine { get; set; }

    public CitClassification CitClassification { get; set; } = CitClassification.NieDotyczy;
    /// <summary>Art. 15c ustawy o CIT: null — automatycznie wg pozycji RZiS (R.H.I odsetki → koszt, R.G.II → przychód odsetkowy).</summary>
    public DebtFinancingKind? DebtFinancing { get; set; }
    /// <summary>Kategoria RPP (null — wg pozycji bilansu / RZiS).</summary>
    public CashFlowCategory? CashFlow { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsBlocked { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public string SyntheticNumber => Number.Split('-')[0];

    public bool IsResultAccount => Type is AccountType.WynikoweKosztowe or AccountType.WynikowePrzychodowe;
    public bool IsBalanceAccount => Type is AccountType.Aktywne or AccountType.Pasywne or AccountType.AktywnoPasywne;

    public static ValidationResult ValidateNumber(string number)
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(number)) return r.Error("Numer konta jest wymagany.");
        var parts = number.Split('-');
        if (parts[0].Length != 3 || !parts[0].All(char.IsDigit))
            r.Error($"Konto „{number}”: symbol syntetyczny musi składać się z 3 cyfr (np. 201).");
        for (var i = 1; i < parts.Length; i++)
            if (parts[i].Length == 0 || parts[i].Length > 10 || !parts[i].All(char.IsLetterOrDigit))
                r.Error($"Konto „{number}”: segment analityki „{parts[i]}” jest nieprawidłowy (1–10 znaków alfanumerycznych).");
        if (parts.Length > 5) r.Error("Maksymalnie 4 poziomy analityki.");
        return r;
    }

    public static AccountGroup GroupFromNumber(string number) => (AccountGroup)(number[0] - '0');
}
