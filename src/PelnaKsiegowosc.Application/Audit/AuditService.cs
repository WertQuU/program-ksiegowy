using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Audit;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Application.Audit;

/// <summary>
/// Dziennik audytowy (append-only, łańcuch SHA-256). Wpis dodawany jest do bieżącej jednostki pracy
/// (SaveChanges wykonuje wywołujący), dzięki czemu operacja i jej ślad audytowy zapisują się atomowo.
/// </summary>
public sealed class AuditService
{
    private readonly KsiegiDbContext _db;
    private readonly IClock _clock;

    public AuditService(KsiegiDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<AuditEvent> AddAsync(ICurrentUser? user, string action, string entityType, string? entityId, string? details, CancellationToken ct = default)
    {
        var pending = _db.ChangeTracker.Entries<AuditEvent>().Where(e => e.State == EntityState.Added).Select(e => e.Entity)
            .OrderByDescending(e => e.Sequence).FirstOrDefault();
        AuditEvent? last = pending;
        if (last is null)
        {
            var lastSeq = await _db.AuditEvents.AsNoTracking().MaxAsync(a => (long?)a.Sequence, ct);
            if (lastSeq is not null)
                last = await _db.AuditEvents.AsNoTracking().FirstAsync(a => a.Sequence == lastSeq, ct);
        }
        var ev = new AuditEvent
        {
            Sequence = (last?.Sequence ?? 0) + 1,
            CompanyId = user?.CompanyId is { } c && c != Guid.Empty ? c : null,
            AtUtc = _clock.UtcNow,
            UserId = user?.UserId is { } u && u != Guid.Empty ? u : null,
            UserLogin = user?.Login ?? "system",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = Truncate(details, 8000),
            MachineName = Environment.MachineName,
            PreviousHash = last?.Hash
        };
        ev.Hash = ev.ComputeHash();
        _db.AuditEvents.Add(ev);
        return ev;
    }

    /// <summary>Weryfikuje ciągłość i skróty dziennika audytowego.</summary>
    public async Task<List<string>> VerifyChainAsync(CancellationToken ct = default)
    {
        var problems = new List<string>();
        string? prev = null;
        long expected = 1;
        await foreach (var e in _db.AuditEvents.AsNoTracking().OrderBy(a => a.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (e.Sequence != expected) problems.Add($"Dziennik audytowy: luka w numeracji (oczekiwano {expected}, jest {e.Sequence}).");
            if (e.PreviousHash != prev) problems.Add($"Dziennik audytowy: przerwany łańcuch skrótów przy wpisie {e.Sequence}.");
            if (e.ComputeHash() != e.Hash) problems.Add($"Dziennik audytowy: wpis {e.Sequence} został zmodyfikowany (skrót niezgodny).");
            prev = e.Hash;
            expected = e.Sequence + 1;
        }
        return problems;
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
