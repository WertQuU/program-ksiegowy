using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Warehouse;

namespace PelnaKsiegowosc.Application.Warehouse;

/// <summary>
/// Uproszczona gospodarka magazynowa: kartoteki ilościowo-wartościowe, dokumenty PZ/WZ/RW/PW i różnice inwentaryzacyjne,
/// wycena rozchodu ceną przeciętną ważoną. Rozchód (WZ/RW) i różnice księgowane są automatycznie; przyjęcie (PZ/PW) —
/// gdy podano konto przeciwstawne (zakup zaksięgowany fakturą bezpośrednio na koncie zapasu nie wymaga zapisu PZ).
/// </summary>
public sealed class StockService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public StockService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public async Task<StockItem> AddItemAsync(StockItem item, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name)) throw new DomainException("Podaj kod i nazwę pozycji magazynowej.");
        if (await _db.StockItems.AnyAsync(i => i.CompanyId == _user.CompanyId && i.Code == item.Code.Trim(), ct)) throw new DomainException($"Pozycja o kodzie {item.Code} już istnieje.");
        item.Code = item.Code.Trim();
        item.CompanyId = _user.CompanyId;
        item.Quantity = 0;
        item.Value = 0;
        item.CreatedAtUtc = _clock.UtcNow;
        _db.StockItems.Add(item);
        await new AuditService(_db, _clock).AddAsync(_user, "MAGAZYN_POZYCJA", "StockItem", item.Id.ToString(), $"{item.Code} {item.Name}", ct);
        await _db.SaveChangesAsync(ct);
        return item;
    }

    /// <param name="unitPrice">Cena jednostkowa — wymagana dla PZ i PW; dla nadwyżki domyślnie cena przeciętna.</param>
    /// <param name="offsetAccount">Konto przeciwstawne przyjęcia (PZ: np. 300/210; PW: np. 490/580). Puste przy PZ = bez zapisu.</param>
    public async Task<StockMovement> RegisterAsync(StockMovementType type, Guid itemId, DateOnly date, decimal quantity, decimal? unitPrice = null,
        string? reference = null, Guid? counterpartyId = null, string? offsetAccount = null, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        if (quantity <= 0) throw new DomainException("Ilość musi być dodatnia.");
        var item = await _db.StockItems.FirstAsync(i => i.Id == itemId && i.CompanyId == _user.CompanyId, ct);
        if (await _db.StockMovements.AnyAsync(m => m.StockItemId == itemId && m.Date > date, ct))
            throw new DomainException("Na tej pozycji są dokumenty z późniejszą datą — wprowadzaj dokumenty magazynowe chronologicznie (wycena średnią ważoną).");
        var inbound = type is StockMovementType.PZ or StockMovementType.PW or StockMovementType.InwNadwyzka;
        decimal value;
        if (inbound)
        {
            var price = unitPrice ?? (type == StockMovementType.InwNadwyzka ? item.AveragePrice : throw new DomainException("Podaj cenę jednostkową przyjęcia (cena nabycia / koszt wytworzenia)."));
            if (price < 0) throw new DomainException("Cena nie może być ujemna.");
            value = Money.Round(quantity * price);
        }
        else
        {
            if (quantity > item.Quantity) throw new DomainException($"Brak wystarczającej ilości: na stanie {item.Quantity:0.###} {item.Unit}.");
            value = quantity == item.Quantity ? item.Value : Money.Round(quantity * item.Value / item.Quantity);
        }
        if (type == StockMovementType.PW && string.IsNullOrWhiteSpace(offsetAccount)) throw new DomainException("Przyjęcie z produkcji wymaga konta przeciwstawnego (np. 490 lub 580).");

        item.Quantity += inbound ? quantity : -quantity;
        item.Value += inbound ? value : -value;
        var count = await _db.StockMovements.CountAsync(m => m.CompanyId == _user.CompanyId && m.Type == type && m.Date.Year == date.Year, ct);
        var number = $"{(type is StockMovementType.InwNadwyzka or StockMovementType.InwNiedobor ? "INW" : type.ToString())}/{count + 1}/{date.Year}";
        var mv = new StockMovement
        {
            CompanyId = _user.CompanyId, Number = number, Type = type, Date = date, StockItemId = item.Id, Quantity = quantity,
            UnitPrice = Math.Round(value / quantity, 4), Value = value, QuantityAfter = item.Quantity, ValueAfter = item.Value,
            Reference = reference, CounterpartyId = counterpartyId, UserId = _user.UserId, CreatedAtUtc = _clock.UtcNow
        };
        var lines = type switch
        {
            StockMovementType.WZ or StockMovementType.RW => new[] { new LineInput(item.IssueAccount, value, 0), new LineInput(item.InventoryAccount, 0, value) },
            StockMovementType.InwNiedobor => new[] { new LineInput("761-03", value, 0, Description: "Niedobór inwentaryzacyjny"), new LineInput(item.InventoryAccount, 0, value) },
            StockMovementType.InwNadwyzka => new[] { new LineInput(item.InventoryAccount, value, 0), new LineInput("760-03", 0, value, Description: "Nadwyżka inwentaryzacyjna") },
            _ when !string.IsNullOrWhiteSpace(offsetAccount) => new[] { new LineInput(item.InventoryAccount, value, 0), new LineInput(offsetAccount.Trim(), 0, value, counterpartyId) },
            _ => null
        };
        if (lines is not null && value != 0)
        {
            var e = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date,
                $"{number}: {item.Name} {quantity:0.###} {item.Unit}{(reference is null ? "" : $" ({reference})")}", type.ToString(), number, lines), ct);
            mv.JournalEntryId = e.Id;
        }
        _db.StockMovements.Add(mv);
        await new AuditService(_db, _clock).AddAsync(_user, "MAGAZYN_DOKUMENT", "StockMovement", mv.Id.ToString(), $"{number}; {item.Code}; {quantity}; {value}", ct);
        await _db.SaveChangesAsync(ct);
        return mv;
    }

    /// <summary>Stan pozycji na dzień (z historii dokumentów).</summary>
    public async Task<List<(StockItem Item, decimal Quantity, decimal Value)>> StateAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var items = await _db.StockItems.AsNoTracking().Where(i => i.CompanyId == _user.CompanyId).OrderBy(i => i.Code).ToListAsync(ct);
        var last = (await _db.StockMovements.AsNoTracking().Where(m => m.CompanyId == _user.CompanyId && m.Date <= asOf).ToListAsync(ct))
            .GroupBy(m => m.StockItemId).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Date).ThenBy(m => m.CreatedAtUtc).Last());
        return items.Select(i => last.TryGetValue(i.Id, out var m) ? (i, m.QuantityAfter, m.ValueAfter) : (i, 0m, 0m)).ToList();
    }
}
