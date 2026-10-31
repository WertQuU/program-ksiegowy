using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Ledger;

namespace PelnaKsiegowosc.Infrastructure.Integrity;

/// <summary>
/// Lokalny rejestr kontrolny: TYLKO identyfikatory i SHA-256 zaksięgowanych zapisów.
/// Nie jest bazą księgową, kopią roboczą ani alternatywnym źródłem danych.
/// Pierwsze uruchomienie ustanawia punkt odniesienia dopiero po walidacji łańcucha
/// i wymaga niezależnego potwierdzenia przez administratora.
/// </summary>
public sealed class LocalLedgerMirror
{
    public enum MirrorStatus { BaselineCreated, Synchronized, Divergence }

    public sealed record Result(MirrorStatus Status, string Message, int KnownEntries,
        int NewEntries, IReadOnlyList<string> Problems);

    private sealed record Snapshot(string EntryId, string FiscalYearId, long Sequence, string Digest);

    private readonly string _path;

    public LocalLedgerMirror(string dataDirectory, DatabaseProfile profile, Guid companyId)
    {
        if (profile.Engine != DatabaseEngine.PostgreSql || companyId == Guid.Empty ||
            string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Database))
            throw new InvalidOperationException("Rejestr kontrolny wymaga aktywnej firmy, hosta i nazwy zewnętrznej bazy PostgreSQL.");
        // Bez danych logowania: oddzielny rejestr na każdą instancję serwera/bazy i firmę.
        var identity = $"{profile.Host.Trim().ToLowerInvariant()}:{profile.Port}/{profile.Database.Trim()}/{companyId:N}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        _path = Path.Combine(dataDirectory, "KontrolaIntegralnosci", id + ".sqlite");
    }

    public string FilePath => _path;

    /// <summary>
    /// Pobiera jeden spójny stan PostgreSQL w transakcji REPEATABLE READ.
    /// Najpierw sprawdza kompletność łańcucha na serwerze i zgodność z zapisami lokalnymi;
    /// dopiero po udanym porównaniu dopisuje nowe zaksięgowane rekordy do SQLite.
    /// Nigdy nie nadpisuje wcześniejszych odcisków ani niczego w PostgreSQL.
    /// </summary>
    public async Task<Result> CompareAsync(KsiegiDbContext db, Guid companyId,
        CancellationToken ct = default)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("Główne księgi muszą być prowadzone w zewnętrznym PostgreSQL.");
        await using var pg = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var years = await db.FiscalYears.AsNoTracking()
            .Where(y => y.CompanyId == companyId).OrderBy(y => y.StartDate).ToListAsync(ct);
        var journal = await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.CompanyId == companyId && e.Status == EntryStatus.Zaksiegowany)
            .ToListAsync(ct);
        var problems = new List<string>();
        var snapshot = new List<Snapshot>();
        foreach (var year in years)
        {
            string? previous = null;
            long sequence = 1;
            foreach (var entry in journal.Where(e => e.FiscalYearId == year.Id).OrderBy(e => e.SequenceNumber))
            {
                if (entry.SequenceNumber != sequence)
                    problems.Add($"Rok {year.Name}: brak ciągłości numeracji (pozycja {sequence}).");
                if (!string.Equals(entry.PreviousHash, previous, StringComparison.Ordinal))
                    problems.Add($"Rok {year.Name}, pozycja {entry.SequenceNumber}: niespójny łańcuch skrótów.");
                if (entry.Hash is null || !string.Equals(entry.ComputeHash(), entry.Hash, StringComparison.Ordinal))
                    problems.Add($"Rok {year.Name}, pozycja {entry.SequenceNumber}: zmieniono treść dekretu.");
                if (entry.Lines.Count == 0 || entry.TotalDebit != entry.TotalCredit)
                    problems.Add($"Rok {year.Name}, pozycja {entry.SequenceNumber}: zapis nie jest zbilansowany.");
                snapshot.Add(new Snapshot(entry.Id.ToString("N"), year.Id.ToString("N"),
                    entry.SequenceNumber ?? 0, entry.ComputeHash()));
                previous = entry.Hash;
                sequence++;
            }
        }
        if (journal.Count != snapshot.Count)
            problems.Add("Niektóre zaksięgowane zapisy nie są przypisane do istniejącego roku obrotowego.");
        if (problems.Count != 0)
            return new Result(MirrorStatus.Divergence,
                "Główne księgi PostgreSQL nie przeszły kontroli wewnętrznej. Skontaktuj się z administratorem; program pozostaje dostępny.",
                0, 0, problems.Take(20).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var local = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString());
        await local.OpenAsync(ct);
        await using var localTx = await local.BeginTransactionAsync(ct);
        async Task SqlAsync(string sql)
        {
            await using var cmd = local.CreateCommand();
            cmd.Transaction = (SqliteTransaction)localTx;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await SqlAsync("CREATE TABLE IF NOT EXISTS ledger_snapshot (entry_id TEXT PRIMARY KEY, fiscal_year_id TEXT NOT NULL, sequence_no INTEGER NOT NULL, digest TEXT NOT NULL)");
        await SqlAsync("CREATE TABLE IF NOT EXISTS metadata (name TEXT PRIMARY KEY, value TEXT NOT NULL)");

        bool baselineInitialized;
        await using (var flag = local.CreateCommand())
        {
            flag.Transaction = (SqliteTransaction)localTx;
            flag.CommandText = "SELECT COUNT(*) FROM metadata WHERE name='baseline_initialized'";
            baselineInitialized = Convert.ToInt64(await flag.ExecuteScalarAsync(ct)) != 0;
        }
        var stored = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        await using (var read = local.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)localTx;
            read.CommandText = "SELECT entry_id, fiscal_year_id, sequence_no, digest FROM ledger_snapshot";
            await using var rows = await read.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                var entry = new Snapshot(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetString(3));
                stored.Add(entry.EntryId, entry);
            }
        }
        var current = snapshot.ToDictionary(r => r.EntryId, StringComparer.Ordinal);
        foreach (var existing in stored.Values)
        {
            if (!current.TryGetValue(existing.EntryId, out var now))
                problems.Add($"Usunięto zaksięgowany zapis {existing.EntryId}.");
            else if (existing.FiscalYearId != now.FiscalYearId ||
                     existing.Sequence != now.Sequence || existing.Digest != now.Digest)
                problems.Add($"Zmodyfikowano zaksięgowany zapis {existing.EntryId}.");
        }
        if (problems.Count != 0)
            return new Result(MirrorStatus.Divergence,
                "Lokalny punkt odniesienia nie zgadza się z PostgreSQL. Nie zatwierdzono zmian; nie naprawiaj ręcznie ksiąg. Skontaktuj się z administratorem; praca w programie nie jest blokowana.",
                stored.Count, 0, problems.Take(20).ToArray());

        int additions = 0;
        foreach (var record in snapshot)
        {
            if (stored.ContainsKey(record.EntryId)) continue;
            await using var insert = local.CreateCommand();
            insert.Transaction = (SqliteTransaction)localTx;
            insert.CommandText = "INSERT INTO ledger_snapshot (entry_id,fiscal_year_id,sequence_no,digest) VALUES ($id,$year,$sequence,$digest)";
            insert.Parameters.AddWithValue("$id", record.EntryId);
            insert.Parameters.AddWithValue("$year", record.FiscalYearId);
            insert.Parameters.AddWithValue("$sequence", record.Sequence);
            insert.Parameters.AddWithValue("$digest", record.Digest);
            await insert.ExecuteNonQueryAsync(ct);
            additions++;
        }
        await SqlAsync("INSERT OR IGNORE INTO metadata(name,value) VALUES ('baseline_initialized', strftime('%Y-%m-%dT%H:%M:%fZ','now'))");
        await SqlAsync("INSERT INTO metadata(name,value) VALUES ('last_check_utc', strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(name) DO UPDATE SET value=excluded.value");
        await localTx.CommitAsync(ct);
        await pg.CommitAsync(ct);
        if (!baselineInitialized)
            return new Result(MirrorStatus.BaselineCreated,
                "Utworzono pierwszy lokalny punkt odniesienia. Nie potwierdza on braku wcześniejszej manipulacji — administrator powinien zweryfikować stan ksiąg i kopie.",
                0, additions, []);
        return new Result(MirrorStatus.Synchronized,
            additions > 0 ? $"Kontrola poprawna; zapisano {additions} nowych odcisków." : "Kontrola integralności poprawna.",
            stored.Count, additions, []);
    }
}
