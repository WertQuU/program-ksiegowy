using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Audit;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Infrastructure.Backup;

public sealed record ManifestFile(string Path, long Size, string Sha256);

/// <summary>Manifest kopii: wersje, pliki z sumami SHA-256, liczności tabel, sumy kontrolne ksiąg. Nie zawiera sekretów.</summary>
public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public Guid ArchiveId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string AppVersion { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string Engine { get; set; } = "";
    public string? CompanyName { get; set; }
    public string? CompanyNip { get; set; }
    public bool IncludesAttachments { get; set; }
    public bool IncludesDatabaseFile { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
    public List<DataDump.TableInfo> Tables { get; set; } = new();
    public DataDump.ControlSums? ControlSums { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string Reason { get; set; } = "";
}

public sealed record BackupOptions(BackupKey Key, string TargetDirectory, bool IncludeAttachments = true, string Reason = "ręczna", int RetainCount = 0);

public sealed record BackupResult(Guid ArchiveId, string FilePath, long SizeBytes, string Sha256, BackupManifest Manifest, bool Verified, string? RemoteKey, string? RemoteError);

public sealed record VerifyResult(bool Ok, BackupManifest? Manifest, IReadOnlyList<string> Problems);

public sealed record RestoreResult(string DatabasePath, string AttachmentsPath, BackupManifest Manifest, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>Cel zdalny kopii (np. OVHcloud Object Storage).</summary>
public interface IBackupTarget
{
    string Name { get; }
    Task<string> UploadAsync(string localFile, string objectName, string sha256, CancellationToken ct = default);
    Task<IReadOnlyList<(string Key, long Size, DateTime LastModifiedUtc)>> ListAsync(CancellationToken ct = default);
    Task DownloadAsync(string key, string localFile, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Kopie zapasowe: spójna kopia bazy (SQLite Online Backup API) + przenośny zrzut danych + załączniki + konfiguracja
/// (bez sekretów) + manifest. Archiwum ZIP szyfrowane AES-256-GCM (<see cref="ArchiveCrypto"/>), weryfikowane po zapisie.
/// Odtwarzanie zawsze do nowej lokalizacji — nigdy nie nadpisuje bez wyraźnej decyzji użytkownika.
/// </summary>
public sealed class BackupService
{
    public const string Extension = ".pkbak";
    private readonly IClock _clock;
    private readonly string _tempRoot;

    public BackupService(IClock clock, string tempRoot)
    {
        _clock = clock;
        _tempRoot = tempRoot;
        Directory.CreateDirectory(tempRoot);
    }

    public async Task<BackupResult> CreateAsync(KsiegiDbContext db, DatabaseProfile profile, IFileStore attachments, string? configJsonWithoutSecrets,
        BackupOptions options, IBackupTarget? remote = null, CancellationToken ct = default)
    {
        var archiveId = Guid.CreateVersion7();
        var staging = Path.Combine(_tempRoot, "kopia-" + archiveId.ToString("N"));
        Directory.CreateDirectory(options.TargetDirectory);
        var finalName = $"kopia-{SystemClock.ToWarsaw(_clock.UtcNow):yyyyMMdd-HHmmss}-{archiveId.ToString("N")[^8..]}{Extension}";
        var finalPath = Path.Combine(options.TargetDirectory, finalName);
        var partial = finalPath + ".partial";
        var record = new BackupRecord { ArchiveId = archiveId, CreatedAtUtc = _clock.UtcNow, FileName = finalName, Target = options.TargetDirectory, Encrypted = true, Status = "W TOKU" };
        try
        {
            CreatePrivateDirectory(staging);
            var manifest = new BackupManifest
            {
                ArchiveId = archiveId,
                CreatedUtc = _clock.UtcNow,
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "?",
                SchemaVersion = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "",
                Engine = profile.Engine.ToString(),
                IncludesAttachments = options.IncludeAttachments,
                Reason = options.Reason
            };
            // PostgreSQL: jeden spójny snapshot dla każdej tabeli, sum kontrolnych i metadanych
            // załączników. Domyślny READ COMMITTED mógłby odczytać różne stany między zapytaniami.
            if (profile.Engine == DatabaseEngine.PostgreSql && db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("Kopię PostgreSQL należy uruchomić bez istniejącej transakcji, aby wymusić spójny snapshot.");
            await using var snapshot = profile.Engine == DatabaseEngine.PostgreSql
                ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
                : null;
            var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(ct);
            manifest.CompanyName = company?.FullName;
            manifest.CompanyNip = company?.Nip;

            if (profile.Engine == DatabaseEngine.Sqlite)
            {
                var dbDir = Directory.CreateDirectory(Path.Combine(staging, "baza")).FullName;
                // baza (także zaszyfrowana SQLCipher) eksportowana jawnie do katalogu roboczego, który trafia wyłącznie do archiwum
                // szyfrowanego kluczem kopii; katalog roboczy jest usuwany po utworzeniu archiwum
                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync(ct);
                try { SqliteEncryption.ExportPlaintext(conn, Path.Combine(dbDir, "ksiegi.db")); }
                finally { await db.Database.CloseConnectionAsync(); }
                manifest.IncludesDatabaseFile = true;
            }
            manifest.Tables = await DataDump.ExportAsync(db, Path.Combine(staging, "dane"), ct);
            manifest.ControlSums = await DataDump.ControlSumsAsync(db, ct);
            // Pobierz metadane plików z tego samego snapshotu co księgi.
            // Pliki później porównujemy z tymi wersjami SHA-256: równoległa zmiana
            // dowodu nie może przejść do backupu jako rzekomo spójna kopia.
            var snapshotAttachments = options.IncludeAttachments
                ? await db.Attachments.AsNoTracking().ToListAsync(ct)
                : new();
            if (snapshot is not null)
                await snapshot.CommitAsync(ct);

            if (options.IncludeAttachments)
            {
                var attDir = Path.Combine(staging, "zalaczniki");
                foreach (var a in snapshotAttachments)
                {
                    if (!await attachments.ExistsAsync(a.StoragePath, ct)) { manifest.Warnings.Add($"Brak pliku załącznika {a.FileName} ({a.StoragePath})."); continue; }
                    var dest = Path.Combine(attDir, a.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await using (var s = await attachments.OpenAsync(a.StoragePath, ct))
                    await using (var d = File.Create(dest))
                        await s.CopyToAsync(d, ct);
                    var sha = await DataDump.Sha256FileAsync(dest, ct);
                    if (sha != a.Sha256) manifest.Warnings.Add($"Załącznik {a.FileName}: suma SHA-256 niezgodna z bazą (plik uszkodzony lub zmieniony).");
                }
                // Kopia „z załącznikami” musi zawierać komplet nieuszkodzonych dowodów.
                // Samo poprawne odszyfrowanie ZIP i zweryfikowanie obecnych plików nie
                // dowodzi kompletności archiwum względem bazy danych (art. 71 UoR).
                if (manifest.Warnings.Count > 0)
                    throw new InvalidDataException(
                        "Nie można utworzyć kompletnej kopii z załącznikami: " + string.Join("; ", manifest.Warnings));
            }
            if (configJsonWithoutSecrets is not null)
            {
                Directory.CreateDirectory(Path.Combine(staging, "konfiguracja"));
                await File.WriteAllTextAsync(Path.Combine(staging, "konfiguracja", "konfiguracja.json"), configJsonWithoutSecrets, ct);
            }
            foreach (var f in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                manifest.Files.Add(new ManifestFile(Path.GetRelativePath(staging, f).Replace('\\', '/'), new FileInfo(f).Length, await DataDump.Sha256FileAsync(f, ct)));
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);

            // ZIP tworzony w potoku i szyfrowany w locie — jawne archiwum nie powstaje na dysku
            var pipe = new System.IO.Pipelines.Pipe();
            var zipping = Task.Run(async () =>
            {
                try
                {
                    await using var pipeOut = pipe.Writer.AsStream();
                    using (var zip = new ZipArchive(pipeOut, ZipArchiveMode.Create, leaveOpen: true))
                        foreach (var f in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                        {
                            var entry = zip.CreateEntry(Path.GetRelativePath(staging, f).Replace('\\', '/'), CompressionLevel.Optimal);
                            await using var es = entry.Open();
                            await using var fs = File.OpenRead(f);
                            await fs.CopyToAsync(es, ct);
                        }
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex) { await pipe.Writer.CompleteAsync(ex); throw; }
            }, ct);
            await using (var input = pipe.Reader.AsStream())
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                await ArchiveCrypto.EncryptAsync(input, output, options.Key, archiveId, _clock.UtcNow, ct);
            await zipping;

            var verify = await VerifyAsync(partial, options.Key, ct);
            if (!verify.Ok) throw new InvalidOperationException("Weryfikacja kopii po zapisie nie powiodła się: " + string.Join("; ", verify.Problems));
            File.Move(partial, finalPath);
            var archiveSha = await DataDump.Sha256FileAsync(finalPath, ct);
            var size = new FileInfo(finalPath).Length;
            record.Sha256 = archiveSha;
            record.SizeBytes = size;
            record.Verified = true;
            record.Status = manifest.Warnings.Count == 0 ? "OK" : "OK Z OSTRZEŻENIAMI";
            record.Error = manifest.Warnings.Count == 0 ? null : string.Join("; ", manifest.Warnings);

            string? remoteKey = null, remoteError = null;
            if (remote is not null)
            {
                try
                {
                    remoteKey = await remote.UploadAsync(finalPath, finalName, archiveSha, ct);
                    record.RemoteKey = remoteKey;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    remoteError = $"Wysłanie kopii do {remote.Name} nie powiodło się: {ex.Message}";
                    record.Status = "OK LOKALNIE, BŁĄD WYSYŁKI";
                    record.Error = remoteError;
                }
            }
            db.BackupRecords.Add(record);
            await new AuditService(db, _clock).AddAsync(null, "KOPIA_ZAPASOWA", "Backup", archiveId.ToString(), $"{finalName}; {size} B; SHA-256 {archiveSha}; {record.Status}", ct);
            await db.SaveChangesAsync(ct);
            if (options.RetainCount > 0) ApplyRetention(options.TargetDirectory, options.RetainCount);
            return new BackupResult(archiveId, finalPath, size, archiveSha, manifest, true, remoteKey, remoteError);
        }
        catch (Exception ex)
        {
            if (File.Exists(partial)) File.Delete(partial);
            record.Status = "BŁĄD";
            record.Error = ex.Message;
            try
            {
                db.ChangeTracker.Clear();
                db.BackupRecords.Add(record);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception) { /* brak możliwości zapisu stanu — błąd pierwotny ważniejszy */ }
            throw new DomainException($"Kopia zapasowa nie została wykonana: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Katalog roboczy z jawnymi danymi (eksport bazy przed zaszyfrowaniem, rozpakowanie przy weryfikacji) — dostęp tylko
    /// dla właściciela (Linux: 0700; Windows: katalog w profilu użytkownika z dziedziczonymi uprawnieniami tylko dla niego).
    /// </summary>
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// RODO: wygaszenie lokalnych kopii starszych niż wskazana (np. po anonimizacji danych osoby) — usuwa archiwa sprzed
    /// <paramref name="keepFrom"/>; kopia wskazana i nowsze pozostają. Kopie w Object Storage usuwa się zgodnie z ich retencją.
    /// </summary>
    public static int PurgeLocalBackupsOlderThan(string directory, string keepFrom)
    {
        if (!Directory.Exists(directory)) return 0;
        var cutoff = File.GetLastWriteTimeUtc(keepFrom);
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(directory, "*" + Extension).Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(keepFrom), StringComparison.Ordinal)
                                                                                         && File.GetLastWriteTimeUtc(f) < cutoff))
        {
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>Usuwa pozostałości katalogów roboczych po przerwanej operacji (np. awaria zasilania) — wywoływane przy starcie.</summary>
    public static int CleanupStaleTemporaryData(string tempRoot)
    {
        if (!Directory.Exists(tempRoot)) return 0;
        var n = 0;
        foreach (var d in Directory.EnumerateDirectories(tempRoot).Where(d => Path.GetFileName(d) is var name && (name.StartsWith("kopia-") || name.StartsWith("weryfikacja-") || name.StartsWith("odtwarzanie-") || name.StartsWith("migracja-"))))
        {
            try { Directory.Delete(d, true); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var f in Directory.EnumerateFiles(tempRoot, "kopia-*.zip"))
        {
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>Weryfikacja archiwum: odszyfrowanie (uwierzytelnienie każdego fragmentu), rozpakowanie, sumy SHA-256 wszystkich plików z manifestu.</summary>
    public async Task<VerifyResult> VerifyAsync(string archivePath, BackupKey key, CancellationToken ct = default)
    {
        var dir = Path.Combine(_tempRoot, "weryfikacja-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = await ExtractAsync(archivePath, key, dir, ct);
            var problems = await CheckFilesAsync(dir, manifest, ct);
            return new VerifyResult(problems.Count == 0, manifest, problems);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException or IOException)
        {
            return new VerifyResult(false, null, new[] { ex.Message });
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private async Task<BackupManifest> ExtractAsync(string archivePath, BackupKey key, string dir, CancellationToken ct)
    {
        CreatePrivateDirectory(dir);
        var content = Path.Combine(dir, "zawartosc");
        // odszyfrowanie w potoku i rozpakowanie bez zapisu jawnego archiwum ZIP
        var pipe = new System.IO.Pipelines.Pipe();
        var decrypting = Task.Run(async () =>
        {
            try
            {
                await using var input = File.OpenRead(archivePath);
                await using var pipeOut = pipe.Writer.AsStream();
                await ArchiveCrypto.DecryptAsync(input, pipeOut, key, ct);
                await pipe.Writer.CompleteAsync();
            }
            catch (Exception ex) { await pipe.Writer.CompleteAsync(ex); throw; }
        }, ct);
        try
        {
            await using (var zipIn = pipe.Reader.AsStream())
            using (var zip = new ZipArchive(zipIn, ZipArchiveMode.Read))
            {
                // Akceptuj tylko ścieżki potomne katalogu roboczego (bez ../ ani absolutnych ścieżek).
                // limity rozpakowania (archiwum nietypowe lub spreparowane): liczba plików, łączny rozmiar po dekompresji
                // (wielokrotność rozmiaru archiwum, nie więcej niż wolne miejsce na dysku z zapasem) — liczone z faktycznie zapisanych bajtów
                var archiveSize = new FileInfo(archivePath).Length;
                var free = TryFreeSpace(content);
                var maxTotal = Math.Max(MaxExpansionFloor, archiveSize * MaxExpansionRatio);
                if (free is { } f && f - DiskReserve < maxTotal) maxTotal = Math.Max(0, f - DiskReserve);
                long total = 0;
                var count = 0;
                var buffer = new byte[81920];
                var extracted = new HashSet<string>(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    if (++count > MaxArchiveEntries) throw new InvalidDataException($"Archiwum zawiera ponad {MaxArchiveEntries} plików — odtwarzanie przerwane.");
                    if (entry.FullName.EndsWith('/')) continue;
                    if (!IsPathInsideBackupRoot(content, entry.FullName))
                        throw new InvalidDataException($"Niedozwolona ścieżka w archiwum: {entry.FullName}");
                    var target = Path.GetFullPath(Path.Combine(content, entry.FullName));
                    if (!extracted.Add(target))
                        throw new InvalidDataException("Archiwum zawiera powtarzającą się ścieżkę docelową: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var es = entry.Open();
                    await using var fs = File.Create(target);
                    int n;
                    while ((n = await es.ReadAsync(buffer, ct)) > 0)
                    {
                        total += n;
                        if (total > maxTotal)
                            throw new InvalidDataException($"Rozpakowana zawartość archiwum przekracza dopuszczalny rozmiar ({maxTotal / (1024 * 1024)} MiB) lub wolne miejsce na dysku — odtwarzanie przerwane, pliki tymczasowe usunięte.");
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                }
            }
        }
        catch (Exception) when (!decrypting.IsCompletedSuccessfully)
        {
            await decrypting; // przyczyna pierwotna: błędny klucz / uszkodzone archiwum (zamiast błędu ZIP)
            throw;
        }
        await decrypting;
        var manifestPath = Path.Combine(content, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("Archiwum nie zawiera manifestu.");
        return JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, ct))!;
    }

    /// <summary>Maksymalna liczba plików w archiwum kopii.</summary>
    public int MaxArchiveEntries { get; init; } = 500_000;
    /// <summary>Maksymalny rozmiar po rozpakowaniu jako wielokrotność rozmiaru archiwum (nie mniej niż <see cref="MaxExpansionFloor"/>).</summary>
    public long MaxExpansionRatio { get; init; } = 200;
    public long MaxExpansionFloor { get; init; } = 1L << 30;
    public const long DiskReserve = 512L << 20;

    private static long? TryFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Sprawdza granicę katalogu, a nie sam wspólny prefiks nazwy (np. zawartosc i zawartosc-inne).</summary>
    internal static bool IsPathInsideBackupRoot(string rootDirectory, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath)) return false;
        var root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static async Task<List<string>> CheckFilesAsync(string dir, BackupManifest manifest, CancellationToken ct)
    {
        var content = Path.Combine(dir, "zawartosc");
        var problems = new List<string>();
        if (manifest.FormatVersion != 1)
            return new List<string> { "Nieobsługiwana wersja manifestu kopii zapasowej." };
        if (manifest.Files is null || manifest.Tables is null || manifest.ControlSums is null)
            return new List<string> { "Manifest kopii jest niekompletny: brak wykazu plików, tabel lub sum kontrolnych." };
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Path) || !expectedPaths.Add(f.Path))
            {
                problems.Add("Manifest zawiera pustą lub powtórzoną ścieżkę pliku.");
                continue;
            }
            if (!IsPathInsideBackupRoot(content, f.Path))
            {
                problems.Add($"Niedozwolona ścieżka w archiwum: {f.Path}");
                continue;
            }
            var full = Path.GetFullPath(Path.Combine(content, f.Path));
            if (!File.Exists(full)) { problems.Add($"Brak pliku {f.Path}."); continue; }
            if (new FileInfo(full).Length != f.Size) problems.Add($"Plik {f.Path}: niezgodny rozmiar.");
            if (await DataDump.Sha256FileAsync(full, ct) != f.Sha256) problems.Add($"Plik {f.Path}: niezgodna suma SHA-256.");
        }
        var listed = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        if (manifest.Tables.Count == 0)
            problems.Add("Manifest nie zawiera wykazu tabel danych.");
        foreach (var t in manifest.Tables)
        {
            var dataName = "dane/" + t.Table + ".jsonl";
            if (!listed.Contains(dataName))
                problems.Add($"Brak zrzutu tabeli {t.Table} w wykazie plików manifestu.");
        }
        foreach (var extra in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(content, f).Replace('\\', '/')))
            if (extra != "manifest.json" && !listed.Contains(extra)) problems.Add($"Plik spoza manifestu: {extra}.");
        return problems;
    }

    /// <summary>
    /// Odtworzenie kopii do NOWEGO katalogu (baza SQLite + załączniki). Jeżeli katalog zawiera bazę, operacja jest odrzucana.
    /// Po odtworzeniu: migracje schematu, porównanie liczności tabel i sum kontrolnych ksiąg z manifestem.
    /// </summary>
    /// <param name="databaseKey">Klucz SQLCipher dla odtworzonej bazy (64 znaki szesnastkowe) — obowiązkowy: baza trafia do katalogu
    /// docelowego wyłącznie w postaci zaszyfrowanej (jawna kopia robocza powstaje tylko w prywatnym katalogu tymczasowym i jest usuwana).</param>
    /// <param name="attachmentsKey">Klucz szyfrowania załączników magazynu docelowego (256 bitów).</param>
    public async Task<RestoreResult> RestoreToSqliteAsync(string archivePath, BackupKey key, string targetDirectory, string databaseKey, byte[] attachmentsKey, CancellationToken ct = default)
    {
        if (databaseKey is not { Length: 64 } || !databaseKey.All(Uri.IsHexDigit))
            throw new DomainException("Odtwarzanie wymaga klucza szyfrowania bazy — odtworzona baza nie może zostać zapisana jawnie.");
        if (attachmentsKey is not { Length: 32 })
            throw new DomainException("Odtwarzanie wymaga klucza AES-256 dla załączników.");

        var target = Path.GetFullPath(targetDirectory);
        var targetInfo = new DirectoryInfo(target);
        if (targetInfo.Exists && (targetInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                                  targetInfo.EnumerateFileSystemInfos().Any()))
            throw new DomainException("Odtwarzanie wymaga pustego, zwykłego katalogu — istniejące dane nie będą nadpisane.");
        var parent = Path.GetDirectoryName(target)
            ?? throw new DomainException("Nie można odtwarzać ksiąg bezpośrednio do katalogu głównego dysku.");
        var pending = Path.Combine(parent, "." + Path.GetFileName(target) + "-odtwarzanie-" + Guid.NewGuid().ToString("N"));
        var workDir = Path.Combine(_tempRoot, "odtwarzanie-" + Guid.NewGuid().ToString("N"));
        try
        {
            CreatePrivateDirectory(pending);
            var manifest = await ExtractAsync(archivePath, key, workDir, ct);
            var problems = await CheckFilesAsync(workDir, manifest, ct);
            if (problems.Count > 0)
                throw new DomainException("Archiwum jest uszkodzone — odtwarzanie przerwane: " + string.Join("; ", problems));
            var content = Path.Combine(workDir, "zawartosc");
            var pendingAttachments = Path.Combine(pending, "Zalaczniki");
            await RestoreAttachmentsAsync(Path.Combine(content, "zalaczniki"),
                new Application.Documents.LocalFileStore(pendingAttachments, attachmentsKey), ct);

            // Wszystkie operacje wrażliwe wykonaj poza katalogiem docelowym.
            var work = Path.Combine(workDir, "odtwarzana.db");
            var profile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = work };
            if (manifest.IncludesDatabaseFile)
                File.Copy(Path.Combine(content, "baza", "ksiegi.db"), work);
            await using (var db = DbContextFactory.Create(DatabaseEngine.Sqlite, profile.BuildConnectionString(null)))
            {
                await db.Database.MigrateAsync(ct);
                if (!manifest.IncludesDatabaseFile)
                    await DataDump.ImportAsync(db, Path.Combine(content, "dane"), ct,
                        async (candidate, token) =>
                        {
                            var difference = await CompareAsync(candidate, manifest, token);
                            if (difference.Count > 0)
                                throw new InvalidDataException("Odtworzone księgi są niezgodne z manifestem: " + string.Join("; ", difference));
                        });
                problems.AddRange(await CompareAsync(db, manifest, ct));
                if (problems.Count > 0)
                    throw new InvalidDataException("Odtwarzana baza nie przeszła weryfikacji: " + string.Join("; ", problems));
            }
            SqliteConnection.ClearAllPools();
            SqliteEncryption.ExportEncrypted(work, Path.Combine(pending, "ksiegi.db"), databaseKey);

            // Katalog jest przygotowany i zweryfikowany: publikujemy go jednym
            // przeniesieniem na tym samym woluminie, nigdy nie odsłaniając
            // użytkownikowi częściowo odtworzonej bazy i załączników.
            if (Directory.Exists(target))
            {
                if (new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    Directory.EnumerateFileSystemEntries(target).Any())
                    throw new DomainException("Katalog docelowy został zmieniony podczas odtwarzania — odmowa nadpisania.");
                Directory.Delete(target);
            }
            Directory.Move(pending, target);
            return new RestoreResult(Path.Combine(target, "ksiegi.db"), Path.Combine(target, "Zalaczniki"), manifest, problems);
        }
        finally
        {
            TryDelete(workDir);
            TryDelete(pending);
        }
    }

    /// <summary>Odtworzenie kopii do pustej bazy serwerowej (np. PostgreSQL) z przenośnego zrzutu danych.</summary>
    public async Task<RestoreResult> RestoreToDatabaseAsync(string archivePath, BackupKey key, KsiegiDbContext target, IFileStore attachmentsTarget, CancellationToken ct = default)
    {
        var dir = Path.Combine(_tempRoot, "odtwarzanie-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = await ExtractAsync(archivePath, key, dir, ct);
            var problems = await CheckFilesAsync(dir, manifest, ct);
            if (problems.Count > 0) throw new DomainException("Archiwum jest uszkodzone: " + string.Join("; ", problems));
            var content = Path.Combine(dir, "zawartosc");
            await target.Database.MigrateAsync(ct);
            // Odtwarzanie powinno dotyczyć nowej bazy i pustego magazynu, a nie
            // nadpisywać istniejące dowody innej jednostki.
            if ((await DataDump.CountsAsync(target, ct)).Values.Any(n => n != 0))
                throw new DomainException("Odtwarzanie serwera wymaga pustej bazy — istniejące księgi nie będą nadpisane.");
            if (Directory.Exists(attachmentsTarget.RootPath) &&
                Directory.EnumerateFiles(attachmentsTarget.RootPath, "*", SearchOption.AllDirectories)
                    .Any(f => Path.GetFileName(f) != ".szyfrowanie"))
                throw new DomainException("Odtwarzanie wymaga pustego magazynu załączników — istniejące dowody nie będą nadpisane.");
            // Najpierw odtwórz pliki, a bazę zatwierdź dopiero po weryfikacji
            // wszystkich tabel i obrotów wewnątrz transakcji. W razie błędu
            // kopiowania plików baza pozostaje pusta.
            await RestoreAttachmentsAsync(Path.Combine(content, "zalaczniki"), attachmentsTarget, ct);
            await DataDump.ImportAsync(target, Path.Combine(content, "dane"), ct,
                async (candidate, token) =>
                {
                    var differences = await CompareAsync(candidate, manifest, token);
                    if (differences.Count > 0)
                        throw new InvalidDataException("Odtworzone księgi różnią się od kopii: " + string.Join("; ", differences));
                });
            problems.AddRange(await CompareAsync(target, manifest, ct));
            return new RestoreResult("(serwer)", attachmentsTarget.RootPath, manifest, problems);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>Kontrola kompletności po odtworzeniu: liczności tabel i sumy kontrolne ksiąg.</summary>
    public static async Task<List<string>> CompareAsync(KsiegiDbContext db, BackupManifest manifest, CancellationToken ct)
    {
        var problems = new List<string>();
        var counts = await DataDump.CountsAsync(db, ct);
        foreach (var t in manifest.Tables)
        {
            counts.TryGetValue(t.Table, out var c);
            if (c != t.Rows) problems.Add($"Tabela {t.Table}: odtworzono {c} rekordów, w kopii {t.Rows}.");
        }
        if (manifest.ControlSums is { } expected)
        {
            var actual = await DataDump.ControlSumsAsync(db, ct);
            if (actual.PostedEntries != expected.PostedEntries) problems.Add($"Liczba zapisów zaksięgowanych: {actual.PostedEntries} ≠ {expected.PostedEntries}.");
            if (actual.LastHashes != expected.LastHashes) problems.Add("Skróty zapisów dziennika różnią się od kopii.");
            foreach (var (year, d) in expected.DebitByYear)
                if (actual.DebitByYear.GetValueOrDefault(year) != d || actual.CreditByYear.GetValueOrDefault(year) != expected.CreditByYear.GetValueOrDefault(year))
                    problems.Add($"Obroty roku {year} różnią się od kopii.");
        }
        return problems;
    }

    public static IReadOnlyList<FileInfo> ListLocal(string directory) =>
        Directory.Exists(directory)
            ? new DirectoryInfo(directory).GetFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : Array.Empty<FileInfo>();

    private static void ApplyRetention(string directory, int keep)
    {
        foreach (var f in ListLocal(directory).Skip(keep)) f.Delete();
    }

    /// <summary>Załączniki z archiwum zapisywane przez magazyn docelowy (szyfrowanie) — bez jawnych kopii w katalogu docelowym.</summary>
    private static async Task RestoreAttachmentsAsync(string from, IFileStore target, CancellationToken ct)
    {
        if (!Directory.Exists(from)) return;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            await using var s = File.OpenRead(f);
            await target.SaveAtAsync(Path.GetRelativePath(from, f).Replace('\\', '/'), s, ct);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, false);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
