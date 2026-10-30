using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Application.Legal;

/// <summary>Zapamiętana zmiana w przepisach (z monitora) z informacją, czy użytkownik ją przejrzał.</summary>
public sealed record StoredLegalChange(string ActName, string Area, string ChangeId, string Kind, string Title, DateOnly? Published, DateOnly? InForce, string Url, bool Seen);

/// <summary>Przechowywanie wyników monitora przepisów w ustawieniach bazy.</summary>
public sealed class LegalUpdates
{
    public const string ChangesKey = "przepisy.zmiany";
    public const string LastCheckKey = "przepisy.sprawdzono";
    private readonly KsiegiDbContext _db;
    private readonly IClock _clock;

    public LegalUpdates(KsiegiDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<List<StoredLegalChange>> ListAsync(CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync(null, ChangesKey, () => new List<StoredLegalChange>(), ct);

    public Task<DateTime?> LastCheckAsync(CancellationToken ct = default) =>
        new SettingsService(_db, _clock).GetAsync<DateTime?>(null, LastCheckKey, () => null, ct);

    /// <summary>Dopisuje nowe zmiany (zachowując oznaczenia „przejrzane”) i zapisuje czas sprawdzenia.</summary>
    public async Task<int> MergeAsync(IEnumerable<StoredLegalChange> found, CancellationToken ct = default)
    {
        var settings = new SettingsService(_db, _clock);
        var list = await ListAsync(ct);
        var known = list.Select(c => (c.ChangeId, c.Kind)).ToHashSet();
        var added = 0;
        foreach (var f in found.Where(f => !known.Contains((f.ChangeId, f.Kind))))
        {
            list.Add(f with { Seen = false });
            added++;
        }
        await settings.SetAsync(null, ChangesKey, list.OrderByDescending(c => c.Published).ToList(), ct);
        await settings.SetAsync<DateTime?>(null, LastCheckKey, _clock.UtcNow, ct);
        await _db.SaveChangesAsync(ct);
        return added;
    }

    public async Task MarkSeenAsync(string? changeId, CancellationToken ct = default)
    {
        var settings = new SettingsService(_db, _clock);
        var list = (await ListAsync(ct)).Select(c => changeId is null || c.ChangeId == changeId ? c with { Seen = true } : c).ToList();
        await settings.SetAsync(null, ChangesKey, list, ct);
        await _db.SaveChangesAsync(ct);
    }
}
