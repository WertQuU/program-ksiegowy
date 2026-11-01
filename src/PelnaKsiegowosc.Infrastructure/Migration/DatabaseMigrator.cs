using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Backup;

namespace PelnaKsiegowosc.Infrastructure.Migration;

public sealed class MigrationReport
{
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; set; }
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public string? PreMigrationBackup { get; set; }
    public Dictionary<string, (long source, long target)> Tables { get; } = new();
    public int AttachmentsCopied { get; set; }
    public List<string> Problems { get; } = new();
    public bool Success => Problems.Count == 0;

    public string ToText()
    {
        var lines = new List<string>
        {
            $"Raport migracji danych — {(Success ? "ZAKOŃCZONA POWODZENIEM" : "NIEPOWODZENIE")}",
            $"Źródło: {Source}", $"Cel: {Target}",
            $"Start: {StartedUtc.ToPlDateTime()}, koniec: {FinishedUtc.ToPlDateTime()}",
            $"Kopia źródła przed migracją: {PreMigrationBackup ?? "brak"}",
            $"Załączniki skopiowane i zweryfikowane: {AttachmentsCopied}",
            "Tabele (źródło / cel):"
        };
        lines.AddRange(Tables.OrderBy(t => t.Key).Select(t => $"  {t.Key}: {t.Value.source} / {t.Value.target}{(t.Value.source == t.Value.target ? "" : "  ← NIEZGODNOŚĆ")}"));
        if (Problems.Count > 0) { lines.Add("Problemy:"); lines.AddRange(Problems.Select(p => "  • " + p)); }
        lines.Add("Baza źródłowa NIE została usunięta ani zmieniona.");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Migracja danych między silnikami (SQLite ↔ PostgreSQL): kopia źródła, import w jednej transakcji do pustej bazy docelowej
/// z zachowaniem identyfikatorów, kontrola liczności, sum kontrolnych ksiąg, łańcuchów skrótów, spójności referencyjnej
/// i sum SHA-256 załączników. Źródło nie jest usuwane. Przełączenie aktywnej bazy wykonuje użytkownik świadomie.
/// </summary>
public sealed class DatabaseMigrator
{
    private readonly IClock _clock;
    private readonly string _tempRoot;

    public DatabaseMigrator(IClock clock, string tempRoot)
    {
        _clock = clock;
        _tempRoot = tempRoot;
    }

    public async Task<MigrationReport> MigrateAsync(KsiegiDbContext source, string sourceDescription, IFileStore sourceAttachments,
        KsiegiDbContext target, string targetDescription, IFileStore targetAttachments, Func<Task<string>> createSourceBackup, CancellationToken ct = default)
    {
        var report = new MigrationReport { StartedUtc = _clock.UtcNow, Source = sourceDescription, Target = targetDescription };
        var dumpDir = Path.Combine(_tempRoot, "migracja-" + Guid.NewGuid().ToString("N"));
        // pełna kontrola magazynu załączników przed migracją — każdy zaszyfrowany plik musi otwierać się kluczem stanowiska
        if (sourceAttachments is Application.Documents.LocalFileStore { Encrypted: true } local && local.Check() is { Ok: false } bad)
            throw new DomainException($"Magazyn załączników zawiera pliki nieczytelne tym kluczem lub uszkodzone ({bad.Unreadable.Count}, np. {bad.Unreadable[0]}) — migracja wstrzymana. Zaimportuj właściwy klucz albo odtwórz pliki z kopii.");
        try
        {
            report.PreMigrationBackup = await createSourceBackup();
            if ((await target.Database.GetPendingMigrationsAsync(ct)).Any())
            {
                if (target.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                    throw new DomainException("Przygotuj schemat PostgreSQL przed importem danych. Operator nie wykonuje migracji schematu.");
                await target.Database.MigrateAsync(ct);
            }
            BackupService.CreatePrivateDirectory(dumpDir);
            var tables = await DataDump.ExportAsync(source, dumpDir, ct);
            var sourceSums = await DataDump.ControlSumsAsync(source, ct);
            try
            {
                await DataDump.ImportAsync(target, dumpDir, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Problems.Add("Import przerwany — transakcja wycofana, baza docelowa pozostaje pusta: " + (ex.InnerException?.Message ?? ex.Message));
                return report;
            }
            target.ChangeTracker.Clear();
            var targetCounts = await DataDump.CountsAsync(target, ct);
            foreach (var t in tables)
            {
                var tc = targetCounts.GetValueOrDefault(t.Table);
                report.Tables[t.Table] = (t.Rows, tc);
                if (tc != t.Rows) report.Problems.Add($"Tabela {t.Table}: {t.Rows} → {tc}.");
            }
            var targetSums = await DataDump.ControlSumsAsync(target, ct);
            if (targetSums.PostedEntries != sourceSums.PostedEntries || targetSums.LastHashes != sourceSums.LastHashes)
                report.Problems.Add("Sumy kontrolne zapisów (liczba, skróty) różnią się między źródłem a celem.");
            foreach (var (y, d) in sourceSums.DebitByYear)
                if (targetSums.DebitByYear.GetValueOrDefault(y) != d || targetSums.CreditByYear.GetValueOrDefault(y) != sourceSums.CreditByYear.GetValueOrDefault(y))
                    report.Problems.Add($"Obroty roku {y} różnią się po migracji.");

            // integralność ksiąg w bazie docelowej (skróty liczone na danych odczytanych z nowego silnika)
            var integrity = new IntegrityService(target, _clock);
            foreach (var fy in await target.FiscalYears.AsNoTracking().ToListAsync(ct))
                foreach (var p in await integrity.CheckLedgerChainAsync(fy.Id, ct)) report.Problems.Add($"Rok {fy.Name}: {p}");

            // spójność referencyjna (dodatkowo do ograniczeń FK)
            var orphanLines = await target.JournalLines.CountAsync(l => !target.JournalEntries.Any(e => e.Id == l.EntryId), ct);
            var orphanAccounts = await target.JournalLines.CountAsync(l => !target.Accounts.Any(a => a.Id == l.AccountId), ct);
            if (orphanLines + orphanAccounts > 0) report.Problems.Add($"Spójność referencyjna: {orphanLines} pozycji bez zapisu, {orphanAccounts} pozycji bez konta.");

            // załączniki
            foreach (var a in await target.Attachments.AsNoTracking().ToListAsync(ct))
            {
                if (!await sourceAttachments.ExistsAsync(a.StoragePath, ct)) { report.Problems.Add($"Brak pliku załącznika {a.FileName}."); continue; }
                await using var s = await sourceAttachments.OpenAsync(a.StoragePath, ct);
                if (!await targetAttachments.ExistsAsync(a.StoragePath, ct)) await targetAttachments.SaveAtAsync(a.StoragePath, s, ct);
                await using var check = await targetAttachments.OpenAsync(a.StoragePath, ct);
                if (await Application.Documents.LocalFileStore.Sha256Async(check, ct) != a.Sha256) report.Problems.Add($"Załącznik {a.FileName}: suma SHA-256 niezgodna po skopiowaniu.");
                else report.AttachmentsCopied++;
            }
            return report;
        }
        finally
        {
            report.FinishedUtc = _clock.UtcNow;
            try { if (Directory.Exists(dumpDir)) Directory.Delete(dumpDir, true); } catch (IOException) { }
        }
    }
}
