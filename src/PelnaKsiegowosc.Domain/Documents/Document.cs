using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Documents;

/// <summary>Rodzaj dowodu księgowego (art. 20 ust. 2 UoR: zewnętrzne obce, zewnętrzne własne, wewnętrzne; oraz zbiorcze, korygujące, zastępcze, rozliczeniowe — ust. 3).</summary>
public enum DocumentKind
{
    FakturaSprzedazy,
    KorektaSprzedazy,
    FakturaZakupu,
    KorektaZakupu,
    Rachunek,
    WyciagBankowy,
    RaportKasowy,
    KasaPrzyjmie,
    KasaWyda,
    PolecenieKsiegowania,
    NotaKsiegowa,
    DokumentWewnetrzny,
    PrzyjecieSrodkaTrwalego,
    LikwidacjaSrodkaTrwalego,
    AmortyzacjaSrodkowTrwalych,
    DokumentKapitalowy,
    RozliczenieZAkcjonariuszem,
    DowodZastepczy,
    Inny
}

public enum DocumentStatus
{
    /// <summary>Roboczy — edytowalny.</summary>
    Roboczy,
    /// <summary>Zatwierdzony (sprawdzony merytorycznie i formalnie — art. 21 ust. 1 pkt 6 UoR), gotowy do księgowania.</summary>
    Zatwierdzony,
    /// <summary>Zaksięgowany — zmiany wyłącznie przez dokument korygujący.</summary>
    Zaksiegowany,
    /// <summary>Anulowany (dotyczy tylko dokumentów nigdy niezaksięgowanych).</summary>
    Anulowany
}

public enum PaymentStatus
{
    NieDotyczy,
    Nieoplacony,
    CzesciowoOplacony,
    Oplacony,
    Nadplacony
}

public enum PaymentMethod
{
    Przelew,
    Gotowka,
    Karta,
    Kompensata,
    Inna
}

public enum KsefStatus
{
    NieDotyczy,
    DoWyslania,
    Wyslana,
    Przyjeta,
    Odrzucona,
    /// <summary>Faktura otrzymana z KSeF (zakup).</summary>
    Pobrana
}

/// <summary>Oznaczenie faktury w JPK_V7(3): numer KSeF albo OFF / BFK / DI.</summary>
public enum JpkKsefMarker
{
    NrKSeF,
    OFF,
    BFK,
    DI
}

/// <summary>
/// Dowód księgowy. Zawiera elementy z art. 21 ust. 1 UoR: określenie rodzaju i numer, strony operacji,
/// opis operacji i jej wartość, datę dokonania operacji i datę sporządzenia, podpis/akceptację, stwierdzenie
/// zakwalifikowania do ujęcia w księgach (dekretację).
/// </summary>
public class Document : CompanyEntity
{
    public DocumentKind Kind { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Roboczy;

    /// <summary>Numer wewnętrzny (nadany w aplikacji, unikalny w ramach serii).</summary>
    public string Number { get; set; } = "";
    /// <summary>Numer obcy (np. numer faktury dostawcy).</summary>
    public string? ExternalNumber { get; set; }
    public string? NumberingSeries { get; set; }

    public DateOnly IssueDate { get; set; }
    /// <summary>Data dokonania operacji (np. data sprzedaży/dostawy).</summary>
    public DateOnly OperationDate { get; set; }
    /// <summary>Data wpływu (dokumenty obce).</summary>
    public DateOnly? ReceiptDate { get; set; }
    public DateOnly? DueDate { get; set; }

    public Guid? CounterpartyId { get; set; }
    /// <summary>Migawka danych kontrahenta w chwili wystawienia (faktura nie zmienia się po zmianie danych kontrahenta).</summary>
    public string? CounterpartyName { get; set; }
    public string? CounterpartyNip { get; set; }
    public string? CounterpartyAddress { get; set; }
    public string? CounterpartyCountry { get; set; }

    public string Currency { get; set; } = "PLN";
    public decimal ExchangeRate { get; set; } = 1m;
    public DateOnly? ExchangeRateDate { get; set; }
    public string? ExchangeRateTable { get; set; }

    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal NetAmountPln { get; set; }
    public decimal VatAmountPln { get; set; }
    public decimal GrossAmountPln { get; set; }

    public string Description { get; set; } = "";
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Przelew;
    public string? BankAccountIban { get; set; }
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.NieDotyczy;

    /// <summary>Dokument korygowany (dla faktur korygujących).</summary>
    public Guid? CorrectedDocumentId { get; set; }
    public string? CorrectionReason { get; set; }

    /// <summary>Mechanizm podzielonej płatności, metoda kasowa i inne oznaczenia na fakturze.</summary>
    public bool SplitPayment { get; set; }
    public bool CashMethod { get; set; }
    public bool ReverseCharge { get; set; }
    public bool SelfBilling { get; set; }

    public string? KsefNumber { get; set; }
    public KsefStatus KsefStatus { get; set; } = KsefStatus.NieDotyczy;
    public JpkKsefMarker? JpkMarker { get; set; }
    public string? KsefReferenceNumber { get; set; }

    /// <summary>Okres rozliczenia VAT (pierwszy dzień miesiąca), jeżeli dokument wpływa na VAT.</summary>
    public DateOnly? VatPeriod { get; set; }
    public Vat.VatTransactionType VatTransactionType { get; set; } = Vat.VatTransactionType.Krajowa;
    /// <summary>Zakup: świadome potwierdzenie przez użytkownika prawa (i zakresu) odliczenia VAT — bez niego dokument nie może zostać zatwierdzony.</summary>
    public bool VatDeductionConfirmed { get; set; }
    /// <summary>Oznaczenia procedur JPK (np. „MPP”, „TP”), rozdzielone przecinkami.</summary>
    public string? JpkProcedures { get; set; }
    /// <summary>Oznaczenie typu dokumentu w JPK (np. „RO”, „WEW”, „FP”, „MK”, „VAT_RR”).</summary>
    public string? JpkDocumentType { get; set; }
    /// <summary>Podstawa zwolnienia z VAT (przepis ustawy/aktu wykonawczego lub dyrektywy) — wymagana na fakturze ze stawką „zw” (art. 106e ust. 1 pkt 19).</summary>
    public string? VatExemptionBasis { get; set; }
    /// <summary>Czy dokument nie podlega ewidencji VAT (np. faktura od podatnika zwolnionego — tylko koszt).</summary>
    public bool ExcludedFromVatRegister { get; set; }

    public Guid? JournalEntryId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public Guid? PostedByUserId { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }

    public List<DocumentLine> Lines { get; set; } = new();
    public List<DocumentVatLine> VatLines { get; set; } = new();
    public List<DocumentPostingLine> PostingLines { get; set; } = new();

    public bool IsEditable => Status == DocumentStatus.Roboczy;
    public bool IsInvoice => Kind is DocumentKind.FakturaSprzedazy or DocumentKind.KorektaSprzedazy or DocumentKind.FakturaZakupu or DocumentKind.KorektaZakupu;
    public bool IsSales => Kind is DocumentKind.FakturaSprzedazy or DocumentKind.KorektaSprzedazy;
    public bool IsPurchase => Kind is DocumentKind.FakturaZakupu or DocumentKind.KorektaZakupu or DocumentKind.Rachunek;
    public bool IsCorrection => Kind is DocumentKind.KorektaSprzedazy or DocumentKind.KorektaZakupu;

    public static string KindCode(DocumentKind k) => k switch
    {
        DocumentKind.FakturaSprzedazy => "FS",
        DocumentKind.KorektaSprzedazy => "FKS",
        DocumentKind.FakturaZakupu => "FZ",
        DocumentKind.KorektaZakupu => "FKZ",
        DocumentKind.Rachunek => "RACH",
        DocumentKind.WyciagBankowy => "WB",
        DocumentKind.RaportKasowy => "RK",
        DocumentKind.KasaPrzyjmie => "KP",
        DocumentKind.KasaWyda => "KW",
        DocumentKind.PolecenieKsiegowania => "PK",
        DocumentKind.NotaKsiegowa => "NK",
        DocumentKind.DokumentWewnetrzny => "DW",
        DocumentKind.PrzyjecieSrodkaTrwalego => "OT",
        DocumentKind.LikwidacjaSrodkaTrwalego => "LT",
        DocumentKind.AmortyzacjaSrodkowTrwalych => "AM",
        DocumentKind.DokumentKapitalowy => "KAP",
        DocumentKind.RozliczenieZAkcjonariuszem => "RA",
        DocumentKind.DowodZastepczy => "DZ",
        _ => "INNY"
    };

    public static string KindName(DocumentKind k) => k switch
    {
        DocumentKind.FakturaSprzedazy => "Faktura sprzedaży",
        DocumentKind.KorektaSprzedazy => "Faktura korygująca sprzedaży",
        DocumentKind.FakturaZakupu => "Faktura zakupu",
        DocumentKind.KorektaZakupu => "Faktura korygująca zakupu",
        DocumentKind.Rachunek => "Rachunek",
        DocumentKind.WyciagBankowy => "Wyciąg bankowy",
        DocumentKind.RaportKasowy => "Raport kasowy",
        DocumentKind.KasaPrzyjmie => "KP — kasa przyjmie",
        DocumentKind.KasaWyda => "KW — kasa wyda",
        DocumentKind.PolecenieKsiegowania => "Polecenie księgowania",
        DocumentKind.NotaKsiegowa => "Nota księgowa",
        DocumentKind.DokumentWewnetrzny => "Dokument wewnętrzny",
        DocumentKind.PrzyjecieSrodkaTrwalego => "OT — przyjęcie środka trwałego",
        DocumentKind.LikwidacjaSrodkaTrwalego => "LT — likwidacja środka trwałego",
        DocumentKind.AmortyzacjaSrodkowTrwalych => "Amortyzacja środków trwałych",
        DocumentKind.DokumentKapitalowy => "Dokument kapitałowy (PSA)",
        DocumentKind.RozliczenieZAkcjonariuszem => "Rozliczenie z akcjonariuszem",
        DocumentKind.DowodZastepczy => "Dowód zastępczy",
        _ => "Inny dowód"
    };
}

/// <summary>Pozycja faktury (towar/usługa).</summary>
public class DocumentLine : Entity
{
    public Guid DocumentId { get; set; }
    public int LineNo { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "szt.";
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitNetPrice { get; set; }
    public decimal NetAmount { get; set; }
    public string VatRateCode { get; set; } = "23";
    public decimal VatAmount { get; set; }
    public decimal GrossAmount { get; set; }
    /// <summary>Kod GTU (01–13) dla JPK_V7.</summary>
    public string? GtuCode { get; set; }
    public string? PkwiuOrCn { get; set; }
    /// <summary>Konto kosztów/przychodów proponowane do dekretacji.</summary>
    public Guid? AccountId { get; set; }
}

/// <summary>Podsumowanie stawek VAT dokumentu (podstawa rejestru VAT).</summary>
public class DocumentVatLine : Entity
{
    public Guid DocumentId { get; set; }
    public string VatRateCode { get; set; } = "23";
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal NetAmountPln { get; set; }
    public decimal VatAmountPln { get; set; }
    /// <summary>Zakup: procent odliczenia VAT (0–100). Domyślnie 100 tylko po świadomej kwalifikacji.</summary>
    public decimal DeductionPercent { get; set; } = 100m;
    /// <summary>Zakup: czy dotyczy środków trwałych (K_40/K_41).</summary>
    public bool IsFixedAsset { get; set; }
    /// <summary>Zakup: kwota VAT podlegająca odliczeniu w PLN.</summary>
    public decimal DeductibleVatPln { get; set; }
}

/// <summary>Proponowana dekretacja dokumentu (pozycje Wn/Ma) przed zaksięgowaniem.</summary>
public class DocumentPostingLine : Entity
{
    public Guid DocumentId { get; set; }
    public int LineNo { get; set; }
    public Guid AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal? CurrencyAmount { get; set; }
    public Guid? CounterpartyId { get; set; }
    public string? Description { get; set; }
    /// <summary>Czy pozycja tworzy rozrachunek (należność/zobowiązanie).</summary>
    public bool CreatesOpenItem { get; set; }
    /// <summary>Klasyfikacja CIT pozycji (nadpisuje domyślną z konta).</summary>
    public Accounts.CitClassification? CitClassification { get; set; }
}

/// <summary>Historia operacji na dokumencie (utworzenie, zmiany, zatwierdzenie, księgowanie, korekta).</summary>
public class DocumentHistory : CompanyEntity
{
    public Guid DocumentId { get; set; }
    public DateTime AtUtc { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
}

/// <summary>Załącznik (PDF, XML, obraz). Plik przechowywany w magazynie plików; w bazie metadane i SHA-256.</summary>
public class Attachment : CompanyEntity
{
    public Guid? DocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Ścieżka względna w magazynie załączników (np. „2026/10/{id}.bin”).</summary>
    public string StoragePath { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string? Description { get; set; }
    /// <summary>Czy kopia została wysłana do archiwum Object Storage.</summary>
    public DateTime? ArchivedToCloudAtUtc { get; set; }
}

/// <summary>Licznik numeracji (np. faktur sprzedaży w roku). Kontrola współbieżności przez token wersji.</summary>
public class NumberSequence : CompanyEntity
{
    public string Key { get; set; } = "";
    public long LastValue { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}
