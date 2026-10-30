using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Domain.Warehouse;

public enum StockItemKind { Towar, Material, Produkt }

/// <summary>Metoda wyceny rozchodu (art. 34 ust. 4 UoR).</summary>
public enum StockValuation
{
    /// <summary>Cena przeciętna ważona.</summary>
    SredniaWazona,
    /// <summary>„Pierwsze weszło — pierwsze wyszło”.</summary>
    Fifo
}

/// <summary>Rodzaj dokumentu magazynowego.</summary>
public enum StockMovementType
{
    /// <summary>Przyjęcie zewnętrzne (zakup).</summary>
    PZ,
    /// <summary>Wydanie zewnętrzne (sprzedaż).</summary>
    WZ,
    /// <summary>Rozchód wewnętrzny (zużycie materiałów).</summary>
    RW,
    /// <summary>Przyjęcie wyrobów z produkcji.</summary>
    PW,
    /// <summary>Nadwyżka inwentaryzacyjna.</summary>
    InwNadwyzka,
    /// <summary>Niedobór inwentaryzacyjny.</summary>
    InwNiedobor
}

/// <summary>Kartoteka magazynowa — ilość i wartość; rozchód wyceniany ceną przeciętną ważoną albo metodą FIFO (art. 34 ust. 4 UoR).</summary>
public class StockItem : CompanyEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "szt.";
    public StockItemKind Kind { get; set; }
    /// <summary>Konto zapasu (np. 330 towary, 310 materiały, 600 produkty).</summary>
    public string InventoryAccount { get; set; } = "330";
    /// <summary>Konto rozchodu (np. 731 wartość sprzedanych towarów, 401 zużycie materiałów).</summary>
    public string IssueAccount { get; set; } = "731";
    public StockValuation Valuation { get; set; } = StockValuation.SredniaWazona;
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    public decimal AveragePrice => Quantity == 0 ? 0 : Math.Round(Value / Quantity, 4, MidpointRounding.AwayFromZero);
}

public class StockMovement : CompanyEntity
{
    public string Number { get; set; } = "";
    public StockMovementType Type { get; set; }
    public DateOnly Date { get; set; }
    public Guid StockItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Value { get; set; }
    public decimal QuantityAfter { get; set; }
    public decimal ValueAfter { get; set; }
    public string? Reference { get; set; }
    public Guid? CounterpartyId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
