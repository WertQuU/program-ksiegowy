using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Audit;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Application.Setup;

/// <summary>Ustawienia przechowywane w bazie (nie są w nich zapisywane sekrety).</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly KsiegiDbContext _db;
    private readonly IClock _clock;

    public SettingsService(KsiegiDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<T> GetAsync<T>(Guid? companyId, string key, Func<T> defaultFactory, CancellationToken ct = default)
    {
        var s = await FindAsync(companyId, key, ct);
        if (s is null) return defaultFactory();
        return JsonSerializer.Deserialize<T>(s.Value, Json) ?? defaultFactory();
    }

    public async Task SetAsync<T>(Guid? companyId, string key, T value, CancellationToken ct = default)
    {
        var s = await FindAsync(companyId, key, ct);
        var json = JsonSerializer.Serialize(value, Json);
        if (s is null)
            _db.Settings.Add(new AppSetting { CompanyId = companyId, Key = key, Value = json, UpdatedAtUtc = _clock.UtcNow });
        else
        {
            s.Value = json;
            s.UpdatedAtUtc = _clock.UtcNow;
        }
    }

    private async Task<AppSetting?> FindAsync(Guid? companyId, string key, CancellationToken ct)
    {
        var local = _db.Settings.Local.FirstOrDefault(x => x.CompanyId == companyId && x.Key == key);
        return local ?? await _db.Settings.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.Key == key, ct);
    }

    /// <summary>Identyfikator instancji bazy — używany m.in. do wyszukania klucza szyfrowania bazy w magazynie sekretów.</summary>
    public async Task<Guid> GetOrCreateDatabaseIdAsync(CancellationToken ct = default)
    {
        var id = await GetAsync<Guid?>(null, "database.id", () => null, ct);
        if (id is { } g) return g;
        var n = Guid.NewGuid();
        await SetAsync<Guid?>(null, "database.id", n, ct);
        await _db.SaveChangesAsync(ct);
        return n;
    }
}
