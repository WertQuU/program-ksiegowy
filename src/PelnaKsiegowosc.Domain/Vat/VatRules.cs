using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Vat;

/// <summary>Rodzaj transakcji — wpływa na mapowanie do pól JPK_V7 i oznaczenia stawki w FA(3).</summary>
public enum VatTransactionType
{
    Krajowa,
    WewnatrzwspolnotowaDostawaTowarow,
    Eksport,
    UslugiPozaKrajem,
    WewnatrzwspolnotoweNabycieTowarow,
    /// <summary>Import usług z wyłączeniem usług, do których stosuje się art. 28b.</summary>
    ImportUslug,
    /// <summary>Import usług, do których stosuje się art. 28b (B2B — zasada ogólna).</summary>
    ImportUslug28b,
    OdwrotneObciazenieKrajowe,
    ImportTowarow
}

/// <summary>Stawka/oznaczenie VAT obowiązujące w określonym przedziale dat.</summary>
public sealed record VatRate(
    string Code,
    string DisplayName,
    decimal? Percent,
    /// <summary>Wartość pola P_12 w FA(3) (np. „23”, „0 KR”, „zw”, „oo”, „np I”).</summary>
    string Fa3Code,
    DateOnly ValidFrom,
    DateOnly? ValidTo)
{
    public bool IsTaxed => Percent is > 0;
    public bool IsValidOn(DateOnly d) => d >= ValidFrom && (ValidTo is null || d <= ValidTo);
}

/// <summary>Mapowanie kwot do pól ewidencji JPK_V7 (K_xx) dla danej stawki i rodzaju transakcji.</summary>
public sealed record JpkFieldMapping(string? NetField, string? TaxField);

/// <summary>
/// Wersjonowany zestaw reguł VAT. Każdy wpis rejestru VAT zapamiętuje identyfikator wersji reguł, według której został utworzony.
/// Aktualizacja reguł = nowa wersja w kolejnym wydaniu aplikacji (z datą obowiązywania), bez zmiany historycznych wpisów.
/// Źródło: ustawa o VAT (art. 41, 43, 83, 146ef — stawki), rozporządzenie MF ws. JPK_V7 (Dz.U. 2025 poz. 1800 — wymaga weryfikacji brzmienia).
/// </summary>
public static class VatRules
{
    public const string CurrentVersion = "VAT-2026.02";

    private static readonly DateOnly Since2011 = new(2011, 1, 1);

    public static readonly IReadOnlyList<VatRate> Rates = new List<VatRate>
    {
        new("23", "23%", 23m, "23", Since2011, null),
        new("8", "8%", 8m, "8", Since2011, null),
        new("5", "5%", 5m, "5", Since2011, null),
        new("0", "0% (kraj)", 0m, "0 KR", Since2011, null),
        new("0WDT", "0% WDT", 0m, "0 WDT", Since2011, null),
        new("0EX", "0% eksport", 0m, "0 EX", Since2011, null),
        new("zw", "zwolnione", null, "zw", Since2011, null),
        new("oo", "odwrotne obciążenie", null, "oo", Since2011, null),
        new("npI", "np. — poza krajem (art. 100 ust.1 pkt 4 nie)", null, "np I", Since2011, null),
        new("npII", "np. — usługi art. 100 ust. 1 pkt 4", null, "np II", Since2011, null),
    };

    public static VatRate Get(string code, DateOnly onDate)
    {
        var r = Rates.FirstOrDefault(x => x.Code == code && x.IsValidOn(onDate))
                ?? throw new DomainException($"Nieznana lub nieobowiązująca na {onDate.ToPl()} stawka VAT „{code}”.");
        return r;
    }

    public static bool TryGet(string code, DateOnly onDate, out VatRate rate)
    {
        rate = Rates.FirstOrDefault(x => x.Code == code && x.IsValidOn(onDate))!;
        return rate is not null;
    }

    /// <summary>VAT od sumy netto w danej stawce (art. 106e ust. 1 pkt 14 ustawy o VAT), zaokrąglony do grosza.</summary>
    public static decimal TaxFromNet(decimal net, VatRate rate) =>
        rate.Percent is { } p ? Money.Round(net * p / 100m) : 0m;

    /// <summary>Wyliczenie netto/VAT z kwoty brutto (faktury w cenach brutto).</summary>
    public static (decimal net, decimal tax) FromGross(decimal gross, VatRate rate)
    {
        if (rate.Percent is not { } p || p == 0) return (gross, 0m);
        var tax = Money.Round(gross * p / (100m + p));
        return (gross - tax, tax);
    }

    /// <summary>
    /// Pola netto, w których wykazuje się pozycję. K_12 (usługi z art. 100 ust. 1 pkt 4) jest pozycją „w tym” K_11
    /// (dostawa towarów i świadczenie usług poza terytorium kraju) — kwota trafia do obu pól.
    /// </summary>
    public static IEnumerable<string> NetFields(string? field) => field switch
    {
        null => Array.Empty<string>(),
        "K_12" => new[] { "K_11", "K_12" },
        _ => new[] { field }
    };

    /// <summary>Oznaczenia procedur dopuszczalne w JPK_V7(2)/(3). Oznaczenie „MPP” zniesiono od 1.07.2021 — nie trafia do JPK.</summary>
    public static IEnumerable<string> JpkProcedures(string? procedures) =>
        (procedures ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.Equals(p, "MPP", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pola ewidencji sprzedaży JPK_V7(3) — zgodnie z układem JPK_V7M(2)/(3) (K_10–K_36). Wymaga weryfikacji z XSD (3).</summary>
    public static JpkFieldMapping SalesMapping(string rateCode, VatTransactionType type) => (rateCode, type) switch
    {
        ("zw", _) => new("K_10", null),
        ("npI", _) => new("K_11", null),
        ("npII", _) => new("K_12", null),
        ("0", _) => new("K_13", null),
        ("5", VatTransactionType.Krajowa) => new("K_15", "K_16"),
        ("8", VatTransactionType.Krajowa) => new("K_17", "K_18"),
        ("23", VatTransactionType.Krajowa) => new("K_19", "K_20"),
        ("0WDT", _) => new("K_21", null),
        ("0EX", _) => new("K_22", null),
        // „oo” po stronie sprzedawcy: mapowanie niepotwierdzone z XSD JPK_V7M(3) — wymaga ręcznej klasyfikacji.
        _ => new(null, null)
    };

    /// <summary>Podatek należny wykazywany przez nabywcę (WNT, import usług, odwrotne obciążenie krajowe).</summary>
    public static JpkFieldMapping PurchaseSelfAssessedMapping(VatTransactionType type) => type switch
    {
        VatTransactionType.WewnatrzwspolnotoweNabycieTowarow => new("K_23", "K_24"),
        VatTransactionType.ImportUslug => new("K_27", "K_28"),
        VatTransactionType.ImportUslug28b => new("K_29", "K_30"),
        // Odwrotne obciążenie krajowe (nabywca) — K_31/K_32 wg układu JPK_V7(2); WYMAGA WERYFIKACJI z XSD (3).
        VatTransactionType.OdwrotneObciazenieKrajowe => new("K_31", "K_32"),
        VatTransactionType.ImportTowarow => new("K_25", "K_26"),
        _ => new(null, null)
    };

    /// <summary>Pola ewidencji zakupów (K_40–K_43).</summary>
    public static JpkFieldMapping PurchaseMapping(bool fixedAsset) => fixedAsset ? new("K_40", "K_41") : new("K_42", "K_43");

    /// <summary>
    /// Typowe ograniczenia prawa do odliczenia — aplikacja ich NIE stosuje automatycznie; ostrzega i wymaga decyzji użytkownika.
    /// </summary>
    public static IReadOnlyList<string> DeductionWarnings(string? description)
    {
        var w = new List<string>();
        var d = (description ?? "").ToLowerInvariant();
        if (d.Contains("hotel") || d.Contains("nocleg") || d.Contains("gastronom") || d.Contains("restaurac"))
            w.Add("Usługi noclegowe i gastronomiczne — co do zasady brak prawa do odliczenia (art. 88 ust. 1 pkt 4 lit. b ustawy o VAT).");
        if (d.Contains("paliw") || d.Contains("samoch") || d.Contains("leasing"))
            w.Add("Wydatki na samochody osobowe — ograniczenie odliczenia do 50% (art. 86a ustawy o VAT), chyba że pojazd jest wykorzystywany wyłącznie do działalności (VAT-26).");
        return w;
    }
}

public enum VatRegisterType
{
    Sprzedaz,
    Zakup
}

/// <summary>Wpis ewidencji VAT (art. 109 ust. 3 ustawy o VAT) — podstawa rejestrów i JPK_V7.</summary>
public class VatRegisterEntry : CompanyEntity
{
    public Guid DocumentId { get; set; }
    public VatRegisterType Register { get; set; }
    /// <summary>Okres rozliczenia (pierwszy dzień miesiąca).</summary>
    public DateOnly Period { get; set; }
    public int OrdinalNumber { get; set; }
    public string DocumentNumber { get; set; } = "";
    public DateOnly IssueDate { get; set; }
    public DateOnly OperationDate { get; set; }
    public DateOnly? ReceiptDate { get; set; }
    public string CounterpartyName { get; set; } = "";
    public string? CounterpartyNip { get; set; }
    public string CounterpartyCountry { get; set; } = "PL";
    public string VatRateCode { get; set; } = "";
    public VatTransactionType TransactionType { get; set; }
    public decimal NetPln { get; set; }
    public decimal VatPln { get; set; }
    /// <summary>Zakup: kwota podatku naliczonego do odliczenia.</summary>
    public decimal DeductibleVatPln { get; set; }
    /// <summary>Pole JPK dla netto i podatku (np. K_19/K_20).</summary>
    public string? JpkNetField { get; set; }
    public string? JpkTaxField { get; set; }
    public string? GtuCodes { get; set; }
    /// <summary>Oznaczenia procedur (np. „MPP”, „TP”).</summary>
    public string? Procedures { get; set; }
    /// <summary>Typ dokumentu w JPK (np. „RO”, „WEW”, „FP”, „MK”, „VAT_RR”).</summary>
    public string? JpkDocumentType { get; set; }
    public string? KsefNumber { get; set; }
    public Documents.JpkKsefMarker? KsefMarker { get; set; }
    public bool IsFixedAsset { get; set; }
    public string RulesVersion { get; set; } = VatRules.CurrentVersion;
    public bool IsCorrection { get; set; }
}
