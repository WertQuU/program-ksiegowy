using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Ledger;

namespace PelnaKsiegowosc.Infrastructure.Backup;

/// <summary>
/// Przenośny zrzut danych (JSON per tabela) niezależny od silnika bazy. Służy do kopii zapasowej bazy PostgreSQL,
/// do migracji SQLite ↔ PostgreSQL oraz do weryfikacji kompletności po odtworzeniu (liczności rekordów, sumy kontrolne).
/// Identyfikatory (GUID) są zachowywane.
/// </summary>
public static class DataDump
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        IgnoreReadOnlyProperties = true
    };

    /// <summary>Typy encji w kolejności zależności (podmiot nadrzędny przed zależnym).</summary>
    public static IReadOnlyList<IEntityType> OrderedEntityTypes(DbContext db)
    {
        var types = db.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList();
        var result = new List<IEntityType>();
        var visited = new HashSet<IEntityType>();
        void Visit(IEntityType t, HashSet<IEntityType> stack)
        {
            if (visited.Contains(t)) return;
            if (!stack.Add(t)) return; // cykl — pomijamy krawędź
            foreach (var fk in t.GetForeignKeys().Where(fk => fk.PrincipalEntityType != t)) Visit(fk.PrincipalEntityType, stack);
            stack.Remove(t);
            visited.Add(t);
            result.Add(t);
        }
        foreach (var t in types.OrderBy(t => t.GetTableName())) Visit(t, new HashSet<IEntityType>());
        return result;
    }

    public sealed record TableInfo(string Table, string ClrType, long Rows, string Sha256);

    public sealed record ControlSums(Dictionary<string, decimal> DebitByYear, Dictionary<string, decimal> CreditByYear, long PostedEntries, string LastHashes);

    /// <summary>Eksport wszystkich tabel do katalogu (plik JSON Lines na tabelę). Zwraca informację o tabelach.</summary>
    public static async Task<List<TableInfo>> ExportAsync(KsiegiDbContext db, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var infos = new List<TableInfo>();
        foreach (var et in OrderedEntityTypes(db))
        {
            var path = Path.Combine(directory, et.GetTableName() + ".jsonl");
            long rows = 0;
            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            await using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                await foreach (var entity in QueryAll(db, et.ClrType).WithCancellation(ct))
                {
                    await w.WriteLineAsync(JsonSerializer.Serialize(entity, et.ClrType, Json));
                    rows++;
                }
            }
            infos.Add(new TableInfo(et.GetTableName()!, et.ClrType.FullName!, rows, await Sha256FileAsync(path, ct)));
        }
        return infos;
    }

    /// <summary>
    /// Import do pustej bazy (po migracjach) w jednej transakcji — przy błędzie nic nie zostaje zapisane.
    /// </summary>
    public static async Task<Dictionary<string, long>> ImportAsync(KsiegiDbContext db, string directory, CancellationToken ct = default)
    {
        foreach (var et in OrderedEntityTypes(db))
            if (await CountAsync(db, et.ClrType, ct) > 0)
                throw new InvalidOperationException($"Baza docelowa nie jest pusta (tabela {et.GetTableName()}). Import wymaga pustej bazy.");
        var counts = new Dictionary<string, long>();
        var originalAutoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Zapisy zaksięgowane wstawiane są najpierw jako bufor, a status przywracany po zaimportowaniu pozycji —
        // wyzwalacze bazy nie pozwalają dopisać pozycji do zapisu już zaksięgowanego.
        var posted = new List<Guid>();
        foreach (var et in OrderedEntityTypes(db))
        {
            var path = Path.Combine(directory, et.GetTableName() + ".jsonl");
            long n = 0;
            if (File.Exists(path))
            {
                var batch = new List<object>();
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entity = JsonSerializer.Deserialize(line, et.ClrType, Json)!;
                    ClearCollections(entity);
                    if (entity is JournalEntry je && je.Status == EntryStatus.Zaksiegowany) { posted.Add(je.Id); je.Status = EntryStatus.Bufor; }
                    batch.Add(entity);
                    n++;
                    if (batch.Count >= 1000) await FlushAsync(db, batch, ct);
                }
                await FlushAsync(db, batch, ct);
            }
            counts[et.GetTableName()!] = n;
        }
        foreach (var chunk in posted.Chunk(500))
            await db.JournalEntries.Where(e => chunk.Contains(e.Id)).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, EntryStatus.Zaksiegowany), ct);
            await tx.CommitAsync(ct);
            return counts;
        }
        finally
        {
            // Przy błędzie parsowania lub zapisu nie pozostawiaj kontekstu bez
            // wykrywania zmian: mogłoby to wpłynąć na późniejsze księgowania.
            db.ChangeTracker.AutoDetectChangesEnabled = originalAutoDetect;
        }
    }

    private static async Task FlushAsync(KsiegiDbContext db, List<object> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        foreach (var e in batch) db.Entry(e).State = EntityState.Added;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        batch.Clear();
    }

    /// <summary>Kolekcje nawigacyjne są importowane jako osobne tabele — czyszczenie zapobiega podwójnemu wstawieniu.</summary>
    private static void ClearCollections(object entity)
    {
        foreach (var p in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>) && p.GetValue(entity) is System.Collections.IList list)
                list.Clear();
    }

    public static async Task<Dictionary<string, long>> CountsAsync(KsiegiDbContext db, CancellationToken ct = default)
    {
        var d = new Dictionary<string, long>();
        foreach (var et in OrderedEntityTypes(db)) d[et.GetTableName()!] = await CountAsync(db, et.ClrType, ct);
        return d;
    }

    /// <summary>Sumy kontrolne ksiąg: obroty Wn/Ma per rok, liczba zapisów, skróty ostatnich zapisów — porównywane po migracji/odtworzeniu.</summary>
    public static async Task<ControlSums> ControlSumsAsync(KsiegiDbContext db, CancellationToken ct = default)
    {
        var rows = await (from l in db.JournalLines.AsNoTracking()
                          join e in db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                          where e.Status == EntryStatus.Zaksiegowany
                          select new { e.FiscalYearId, l.Debit, l.Credit }).ToListAsync(ct);
        var debit = rows.GroupBy(r => r.FiscalYearId.ToString()).ToDictionary(g => g.Key, g => g.Sum(x => x.Debit));
        var credit = rows.GroupBy(r => r.FiscalYearId.ToString()).ToDictionary(g => g.Key, g => g.Sum(x => x.Credit));
        var posted = await db.JournalEntries.LongCountAsync(e => e.Status == EntryStatus.Zaksiegowany, ct);
        var hashes = await db.JournalEntries.AsNoTracking().Where(e => e.Status == EntryStatus.Zaksiegowany).Select(e => e.Hash).ToListAsync(ct);
        var combined = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", hashes.OrderBy(h => h, StringComparer.Ordinal)))));
        return new ControlSums(debit, credit, posted, combined);
    }

    private static Task<long> CountAsync(DbContext db, Type t, CancellationToken ct) =>
        (Task<long>)typeof(DataDump).GetMethod(nameof(CountGeneric), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t).Invoke(null, new object[] { db, ct })!;

    private static Task<long> CountGeneric<T>(DbContext db, CancellationToken ct) where T : class => db.Set<T>().LongCountAsync(ct);

    private static IAsyncEnumerable<object> QueryAll(DbContext db, Type t) =>
        (IAsyncEnumerable<object>)typeof(DataDump).GetMethod(nameof(QueryGeneric), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t).Invoke(null, new object[] { db })!;

    private static async IAsyncEnumerable<object> QueryGeneric<T>(DbContext db) where T : class
    {
        await foreach (var e in db.Set<T>().AsNoTracking().AsAsyncEnumerable()) yield return e;
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }
}
