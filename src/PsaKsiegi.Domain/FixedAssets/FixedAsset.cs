using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Domain.FixedAssets;

public enum DepreciationMethod
{
    /// <summary>Brak amortyzacji (np. grunty — art. 16c pkt 1 ustawy o CIT, art. 32 ust. 1 UoR).</summary>
    Brak,
    Liniowa,
    /// <summary>Degresywna (art. 16k ust. 1 ustawy o CIT) z przejściem na liniową.</summary>
    Degresywna,
    /// <summary>Jednorazowa (np. środki o niskiej wartości — art. 16d ust. 1 ustawy o CIT, do 10 000 zł).</summary>
    Jednorazowa
}

public enum FixedAssetStatus
{
    WEwidencji,
    CalkowicieZamortyzowany,
    Zlikwidowany,
    Sprzedany
}

public enum FixedAssetCategory
{
    SrodekTrwaly,
    WartoscNiematerialnaIPrawna,
    SrodekTrwalyWBudowie
}

/// <summary>Środek trwały / WNiP z odrębnymi parametrami amortyzacji bilansowej i podatkowej.</summary>
public class FixedAsset : CompanyEntity
{
    public string InventoryNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public FixedAssetCategory Category { get; set; }
    /// <summary>Symbol KŚT (Klasyfikacja Środków Trwałych 2016).</summary>
    public string? KstSymbol { get; set; }
    public DateOnly AcquisitionDate { get; set; }
    /// <summary>Data przyjęcia do używania (wprowadzenia do ewidencji).</summary>
    public DateOnly PlacedInServiceDate { get; set; }
    public Guid? SourceDocumentId { get; set; }
    public Guid? SupplierId { get; set; }

    public decimal InitialValueBook { get; set; }
    public decimal InitialValueTax { get; set; }

    public DepreciationMethod BookMethod { get; set; } = DepreciationMethod.Liniowa;
    public decimal BookRatePercent { get; set; }
    public decimal BookDegressiveFactor { get; set; } = 1m;

    public DepreciationMethod TaxMethod { get; set; } = DepreciationMethod.Liniowa;
    public decimal TaxRatePercent { get; set; }
    public decimal TaxDegressiveFactor { get; set; } = 2m;

    /// <summary>Część odpisów podatkowych niestanowiąca KUP (np. samochody osobowe ponad limit art. 16 ust. 1 pkt 4), w %.</summary>
    public decimal TaxNonDeductiblePercent { get; set; }

    /// <summary>Umorzenie przejęte (np. przy przyjęciu środka używanego z umorzeniem, BO).</summary>
    public decimal OpeningAccumulatedBook { get; set; }
    public decimal OpeningAccumulatedTax { get; set; }
    /// <summary>Miesiąc (pierwszy dzień), od którego obowiązuje umorzenie początkowe — amortyzacja liczona od następnego.</summary>
    public DateOnly? OpeningAccumulatedAsOf { get; set; }

    public string AssetAccountNumber { get; set; } = "010-05";
    public string AccumulatedDepreciationAccountNumber { get; set; } = "070-05";
    public string ExpenseAccountNumber { get; set; } = "400";

    public FixedAssetStatus Status { get; set; } = FixedAssetStatus.WEwidencji;
    public DateOnly? DisposalDate { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<FixedAssetOperation> Operations { get; set; } = new();

    public ValidationResult Validate(decimal lowValueThreshold)
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(InventoryNumber)) r.Error("Numer inwentarzowy jest wymagany.");
        if (string.IsNullOrWhiteSpace(Name)) r.Error("Nazwa środka trwałego jest wymagana.");
        if (InitialValueBook <= 0) r.Error("Wartość początkowa (bilansowa) musi być dodatnia.");
        if (InitialValueTax < 0) r.Error("Wartość początkowa (podatkowa) nie może być ujemna.");
        if (PlacedInServiceDate < AcquisitionDate) r.Error("Data przyjęcia do używania nie może poprzedzać daty nabycia.");
        foreach (var (method, rate, label) in new[] { (BookMethod, BookRatePercent, "bilansowej"), (TaxMethod, TaxRatePercent, "podatkowej") })
        {
            if (method is DepreciationMethod.Liniowa or DepreciationMethod.Degresywna && (rate <= 0 || rate > 100))
                r.Error($"Stawka amortyzacji {label} musi mieścić się w przedziale (0; 100] %.");
        }
        if (TaxMethod == DepreciationMethod.Degresywna && (TaxDegressiveFactor <= 0 || TaxDegressiveFactor > 2m))
            r.Error("Współczynnik metody degresywnej nie może przekraczać 2,0 (art. 16k ust. 1 ustawy o CIT).");
        if (TaxMethod == DepreciationMethod.Jednorazowa && InitialValueTax > lowValueThreshold)
            r.Warning($"Jednorazowy odpis podatkowy dla wartości powyżej {Money.Format(lowValueThreshold)} zł wymaga szczególnej podstawy (np. art. 16k ust. 7 lub 16k ust. 14 ustawy o CIT) — zweryfikuj.");
        if (TaxNonDeductiblePercent is < 0 or > 100) r.Error("Procent odpisów niebędących KUP: 0–100.");
        if (OpeningAccumulatedBook > InitialValueBook || OpeningAccumulatedTax > InitialValueTax)
            r.Error("Umorzenie początkowe nie może przekraczać wartości początkowej.");
        return r;
    }
}

public enum FixedAssetOperationType
{
    Przyjecie,
    Ulepszenie,
    AktualizacjaWyceny,
    OdpisAktualizujacy,
    CzesciowaLikwidacja,
    Likwidacja,
    Sprzedaz,
    ZmianaMiejsca
}

public class FixedAssetOperation : CompanyEntity
{
    public Guid FixedAssetId { get; set; }
    public FixedAssetOperationType Type { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>Zmiana wartości początkowej (ulepszenie +, częściowa likwidacja −).</summary>
    public decimal BookValueChange { get; set; }
    public decimal TaxValueChange { get; set; }
    public string Description { get; set; } = "";
    public Guid? DocumentId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Naliczenie amortyzacji za miesiąc (odrębnie bilansowo i podatkowo).</summary>
public class DepreciationCharge : CompanyEntity
{
    public Guid FixedAssetId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal BookAmount { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>Część odpisu podatkowego niestanowiąca KUP.</summary>
    public decimal TaxNonDeductibleAmount { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? RunId { get; set; }
}

/// <summary>Przebieg naliczania amortyzacji za miesiąc (dokument AM).</summary>
public class DepreciationRun : CompanyEntity
{
    public int Year { get; set; }
    public int Month { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? DocumentId { get; set; }
    public decimal TotalBook { get; set; }
    public decimal TotalTax { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsReversed { get; set; }
}
