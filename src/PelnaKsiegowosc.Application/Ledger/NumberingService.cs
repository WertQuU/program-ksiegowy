using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Documents;

namespace PelnaKsiegowosc.Application.Ledger;

/// <summary>
/// Liczniki numeracji (dzienniki, faktury). Wiersz licznika ma token współbieżności — równoległe pobranie tego samego
/// numeru na serwerze PostgreSQL kończy się wyjątkiem współbieżności zamiast duplikatu numeru.
/// </summary>
public sealed class NumberingService
{
    private readonly KsiegiDbContext _db;

    public NumberingService(KsiegiDbContext db) => _db = db;

    public async Task<long> NextAsync(Guid companyId, string key, CancellationToken ct = default)
    {
        var seq = _db.NumberSequences.Local.FirstOrDefault(s => s.CompanyId == companyId && s.Key == key)
                  ?? await _db.NumberSequences.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == key, ct);
        if (seq is null)
        {
            seq = new NumberSequence { CompanyId = companyId, Key = key, LastValue = 0 };
            _db.NumberSequences.Add(seq);
        }
        seq.LastValue++;
        seq.Version = Guid.NewGuid();
        return seq.LastValue;
    }

    public async Task<long> PeekAsync(Guid companyId, string key, CancellationToken ct = default)
    {
        var seq = await _db.NumberSequences.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == key, ct);
        return (seq?.LastValue ?? 0) + 1;
    }
}
