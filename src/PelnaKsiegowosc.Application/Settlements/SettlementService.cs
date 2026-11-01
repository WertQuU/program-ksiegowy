using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;

namespace PelnaKsiegowosc.Application.Settlements;

public sealed record AgingRow(Guid OpenItemId, string CounterpartyName, string Reference, string Account, DateOnly DocumentDate, DateOnly? DueDate,
    string Currency, decimal Amount, decimal Remaining, decimal RemainingPln, int DaysOverdue, string Bucket);

/// <summary>Rozrachunki: zestawienia należności/zobowiązań z wiekowaniem, kompensaty.</summary>
public sealed class SettlementService
{
    public static readonly (int from, int to, string name)[] Buckets =
    {
        (int.MinValue, 0, "Bieżące"), (1, 30, "1–30 dni"), (31, 60, "31–60 dni"), (61, 90, "61–90 dni"), (91, 180, "91–180 dni"), (181, int.MaxValue, "> 180 dni")
    };

    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public SettlementService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public async Task<List<AgingRow>> AgingAsync(OpenItemSide side, DateOnly asOf, bool overdueOnly = false, Guid? counterpartyId = null, CancellationToken ct = default)
    {
        _user.Require(Permission.ViewData);
        var q = _db.OpenItems.AsNoTracking().Where(o => o.CompanyId == _user.CompanyId && o.Side == side && o.Status != OpenItemStatus.Rozliczona);
        if (counterpartyId is { } c) q = q.Where(o => o.CounterpartyId == c);
        var items = await q.ToListAsync(ct);
        var names = await _db.Counterparties.AsNoTracking().Where(x => x.CompanyId == _user.CompanyId).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var rows = items.Where(o => o.RemainingPln != 0).Select(o =>
        {
            var d = o.DaysOverdue(asOf);
            var bucket = Buckets.First(b => d >= b.from && d <= b.to).name;
            return new AgingRow(o.Id, names.GetValueOrDefault(o.CounterpartyId, "?"), o.Reference, o.AccountNumber, o.DocumentDate, o.DueDate, o.Currency, o.Amount, o.Remaining, o.RemainingPln, d, bucket);
        });
        if (overdueOnly) rows = rows.Where(r => r.DaysOverdue > 0);
        return rows.OrderBy(r => r.CounterpartyName).ThenBy(r => r.DueDate).ToList();
    }

    /// <summary>Kompensata należności i zobowiązania tego samego kontrahenta (nota kompensacyjna) — zapis Wn zobowiązanie / Ma należność.</summary>
    public async Task<JournalEntry> CompensateAsync(Guid receivableItemId, Guid payableItemId, decimal amount, DateOnly date, string noteNumber, CancellationToken ct = default)
    {
        _user.Require(Permission.PostEntries);
        var rec = await _db.OpenItems.FirstAsync(o => o.Id == receivableItemId && o.CompanyId == _user.CompanyId, ct);
        var pay = await _db.OpenItems.FirstAsync(o => o.Id == payableItemId && o.CompanyId == _user.CompanyId, ct);
        if (rec.Side != OpenItemSide.Naleznosc || pay.Side != OpenItemSide.Zobowiazanie) throw new DomainException("Kompensata wymaga należności i zobowiązania.");
        if (rec.CounterpartyId != pay.CounterpartyId) throw new DomainException("Kompensować można rozrachunki tego samego kontrahenta.");
        if (rec.Currency != "PLN" || pay.Currency != "PLN") throw new DomainException("Kompensata rozrachunków walutowych nie jest obsługiwana.");
        if (amount <= 0 || amount > rec.RemainingPln || amount > pay.RemainingPln) throw new DomainException("Kwota kompensaty przekracza saldo jednego z rozrachunków.");
        if (string.IsNullOrWhiteSpace(noteNumber)) throw new DomainException("Podaj numer noty kompensacyjnej (dowód księgowy).");
        // Księgowanie, nadanie źródła alokacjom i aktualizacje opłacenia
        // dokumentów tworzą jedną transakcję. W przeciwnym razie nieudana
        // końcowa aktualizacja pozostawiałaby częściowo zaksięgowaną kompensatę.
        var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var entry = await new LedgerService(_db, _user, _clock).CreateAndPostAsync(new EntryInput("PK", date, date, date,
                $"Kompensata {rec.Reference} z {pay.Reference}", "NK", noteNumber, new List<LineInput>
                {
                    new(pay.AccountNumber, amount, 0, pay.CounterpartyId, $"Kompensata {pay.Reference}", pay.Id),
                    new(rec.AccountNumber, 0, amount, rec.CounterpartyId, $"Kompensata {rec.Reference}", rec.Id)
                }), ct);
            foreach (var allocation in _db.SettlementAllocations.Local.Where(a => a.JournalEntryId == entry.Id))
                allocation.Source = SettlementSource.Kompensata;
            await _db.SaveChangesAsync(ct);
            var docs = new DocumentService(_db, _user, _clock);
            if (rec.DocumentId is { } r) await docs.RefreshPaymentStatusAsync(r, ct);
            if (pay.DocumentId is { } p) await docs.RefreshPaymentStatusAsync(p, ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return entry;
        }
        catch
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
            }
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }
}
