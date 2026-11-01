using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Application.Security;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Configuration;

namespace PelnaKsiegowosc.Infrastructure.Migration;

/// <summary>Import kopii SQLite do pustego, przygotowanego celu. Źródłowy plik pozostaje nietknięty.</summary>
public static class WizardSqliteImport
{
    public static async Task<string> ImportAsync(string sourcePath, string? sourceKey,
        IFileStore sourceFiles, KsiegiDbContext target, IFileStore targetFiles,
        string sourceLogin, string targetLogin, IClock clock, string tempRoot,
        BackupKey backupKey, string backupDirectory, CancellationToken ct = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new DomainException("Nie znaleziono źródłowej bazy SQLite.");
        if (target.Database.IsSqlite() && Path.GetFullPath(target.Database.GetDbConnection().DataSource).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Baza źródłowa i docelowa muszą być różnymi plikami.");
        if ((await target.Database.GetPendingMigrationsAsync(ct)).Any())
            throw new DomainException("Najpierw przygotuj schemat bazy docelowej.");
        if ((await DataDump.CountsAsync(target, ct)).Values.Any(n => n != 0))
            throw new DomainException("Migracja wymaga pustej bazy docelowej. Istniejące dane nie zostaną nadpisane.");
        if (new[] { "-wal", "-shm", "-journal" }.Any(s => File.Exists(sourcePath + s)))
            throw new DomainException("Zamknij program korzystający ze źródłowej bazy SQLite, aby bezpiecznie przenieść spójny plik.");
        var encrypted = SqliteEncryption.IsEncrypted(sourcePath);
        if (encrypted && (sourceKey is not { Length: 64 } || !sourceKey.All(Uri.IsHexDigit)))
            throw new DomainException("Podaj klucz szyfrowania źródłowej bazy SQLite (64 znaki szesnastkowe).");
        var work = Path.Combine(tempRoot, "import-sqlite-" + Guid.NewGuid().ToString("N"));
        BackupService.CreatePrivateDirectory(work);
        try
        {
            var snapshotPath = Path.Combine(work, "snapshot.db");
            // Exclusive access rejects a live source. Copy ciphertext when encrypted;
            // any conversion or schema upgrade operates on this private copy only.
            await using (var original = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
            await using (var snapshotFile = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await original.CopyToAsync(snapshotFile, ct);
            var snapshotProfile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = snapshotPath };
            var secrets = new InMemorySecretStore();
            if (encrypted) secrets.Set(snapshotProfile.PasswordSecretKey, sourceKey!);
            var connector = new DatabaseConnector(secrets);
            await using var snapshot = connector.CreateContext(snapshotProfile);
            await snapshot.Database.MigrateAsync(ct);
            var companies = await snapshot.Companies.AsNoTracking().Take(2).ToListAsync(ct);
            if (companies.Count != 1) throw new DomainException("Źródło musi zawierać księgi dokładnie jednej jednostki.");
            var sourceRoles = await new OperatorAccessService(snapshot, clock).ResolveAsync(
                OperatorAccessService.IdFor(sourceLogin), sourceLogin, companies[0].Id, ct);
            var sourceActor = new CurrentUser(OperatorAccessService.IdFor(sourceLogin), sourceLogin, companies[0].Id, sourceRoles);
            sourceActor.Require(Permission.ManageUsers);
            var integrity = new IntegrityService(snapshot, clock);
            foreach (var year in await snapshot.FiscalYears.AsNoTracking().ToListAsync(ct))
                if ((await integrity.CheckLedgerChainAsync(year.Id, ct)).Count > 0)
                    throw new DomainException("Źródłowe księgi nie przeszły kontroli integralności. Import wstrzymany.");
            var backup = await new BackupService(clock, work).CreateAsync(snapshot, snapshotProfile,
                sourceFiles, null, new BackupOptions(backupKey, backupDirectory, Reason: "przed migracją SQLite w kreatorze"), ct: ct);
            if (!backup.Verified) throw new DomainException("Kopia źródła nie przeszła weryfikacji.");
            var dump = Path.Combine(work, "dump");
            var tables = await DataDump.ExportAsync(snapshot, dump, ct);
            var manifest = new BackupManifest { Tables = tables, ControlSums = await DataDump.ControlSumsAsync(snapshot, ct) };
            foreach (var attachment in await snapshot.Attachments.AsNoTracking().ToListAsync(ct))
            {
                await using var input = await sourceFiles.OpenAsync(attachment.StoragePath, ct);
                if (await LocalFileStore.Sha256Async(input, ct) != attachment.Sha256)
                    throw new DomainException("Źródłowy załącznik nie zgadza się z księgami. Import wstrzymany.");
                if (!await targetFiles.ExistsAsync(attachment.StoragePath, ct))
                {
                    await using var content = await sourceFiles.OpenAsync(attachment.StoragePath, ct);
                    await targetFiles.SaveAtAsync(attachment.StoragePath, content, ct);
                }
                await using var verify = await targetFiles.OpenAsync(attachment.StoragePath, ct);
                if (await LocalFileStore.Sha256Async(verify, ct) != attachment.Sha256)
                    throw new DomainException("Docelowy załącznik nie przeszedł weryfikacji. Import bazy wstrzymany.");
            }
            await DataDump.ImportAsync(target, dump, ct, async (candidate, token) =>
            {
                var differences = await BackupService.CompareAsync(candidate, manifest, token);
                if (differences.Count > 0) throw new DomainException("Migracja nie przeszła weryfikacji: " + string.Join("; ", differences));
                foreach (var year in await candidate.FiscalYears.AsNoTracking().ToListAsync(token))
                    if ((await new IntegrityService(candidate, clock).CheckLedgerChainAsync(year.Id, token)).Count > 0)
                        throw new DomainException("Docelowe księgi nie przeszły kontroli integralności.");
                var targetId = OperatorAccessService.IdFor(targetLogin);
                if (!await candidate.Users.AnyAsync(u => u.Id == targetId, token))
                {
                    var migratedUser = new User
                    {
                        Id = targetId, Login = targetLogin, DisplayName = targetLogin,
                        PasswordAlgorithm = OperatorAccessService.SystemAccount, IsActive = true, CreatedAtUtc = clock.UtcNow
                    };
                    migratedUser.SetRoles(sourceRoles);
                    candidate.Users.Add(migratedUser);
                    await candidate.SaveChangesAsync(token);
                }
                await new OperatorAccessService(candidate, clock).ResolveAsync(targetId, targetLogin, companies[0].Id, token);
            });
            return backup.FilePath;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
