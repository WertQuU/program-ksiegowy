using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Infrastructure.Storage;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Presentation.Services;

/// <param name="NewRecoveryKey">Klucz odzyskiwania wygenerowany przy otwarciu (brak klucza kopii na stanowisku) — do zanotowania przez użytkownika.</param>
public sealed record OpenResult(bool Success, string? Error, string? PreMigrationBackup, string? NewRecoveryKey = null);

/// <summary>
/// Stan uruchomionego programu: ścieżki, konfiguracja stanowiska, magazyn sekretów, aktywna baza, bieżąca jednostka i rok.
/// Każda operacja otwiera krótko żyjący kontekst bazy (<see cref="OpenDb"/>), więc ekrany zawsze widzą aktualne dane.
/// </summary>
public sealed class AppSession
{
    public AppSession(AppPaths paths, AppConfig config, ISecretStore secrets, IClock clock)
    {
        Paths = paths;
        Config = config;
        Secrets = secrets;
        Clock = clock;
        Connector = new DatabaseConnector(secrets);
        User = OperatorIdentity.For(Guid.Empty);
    }

    public AppPaths Paths { get; }
    public AppConfig Config { get; }
    public ISecretStore Secrets { get; }
    public IClock Clock { get; }
    public DatabaseConnector Connector { get; }
    public CurrentUser User { get; private set; }
    private ExclusivePostgresSession? _operatorSession;

    /// <summary>Jedna aktywna instancja programu na login bazy (kilka połączeń SQL w tej instancji jest dozwolonych).</summary>
    public async Task AcquireOperatorSessionAsync(CancellationToken ct = default)
    {
        if (Profile.Engine != DatabaseEngine.PostgreSql)
            throw new DomainException("Aktywne księgi wymagają centralnej bazy PostgreSQL. Lokalny SQLite pozostaje narzędziem migracji i odzyskiwania.");
        if (_operatorSession is { } current)
        {
            if (current.Identity != ExclusivePostgresSession.ProfileIdentity(Profile))
                throw new DomainException("Zmiana serwera lub loginu wymaga ponownego uruchomienia programu.");
            if (!current.IsHealthy)
                throw new DomainException("Utracono blokadę sesji PostgreSQL. Zamknij program i uruchom go ponownie.");
            return;
        }
        _operatorSession = await ExclusivePostgresSession.AcquireAsync(Profile, Connector.ConnectionString(Profile), ct);
    }

    /// <summary>Wyłączna blokada na czas administracyjnej aktualizacji schematu (brak innych aktywnych klientów).</summary>
    public Task EnterMaintenanceAsync(CancellationToken ct = default) =>
        _operatorSession is { IsHealthy: true } active
            ? active.EnterMaintenanceAsync(ct)
            : throw new DomainException("Najpierw uwierzytelnij się do centralnego PostgreSQL.");

    public void ReleaseOperatorSession()
    {
        _operatorSession?.Dispose();
        _operatorSession = null;
    }

    public Company? Company { get; private set; }
    public IReadOnlyList<FiscalYear> Years { get; private set; } = Array.Empty<FiscalYear>();
    public FiscalYear? Year { get; private set; }

    public Guid CompanyId => Company?.Id ?? throw new DomainException("Nie skonfigurowano jednostki — uruchom kreator pierwszego uruchomienia.");
    public FiscalYear CurrentYear => Year ?? throw new DomainException("Brak roku obrotowego.");

    public event EventHandler? ContextChanged;

    /// <summary>Prośba o przejście do sekcji (np. z listy „Do zrobienia”); opcjonalna czynność, np. „nowy” dokument.</summary>
    public event Action<PelnaKsiegowosc.Application.Assistant.AppArea, string?>? NavigationRequested;

    public void Navigate(PelnaKsiegowosc.Application.Assistant.AppArea area, string? action = null) => NavigationRequested?.Invoke(area, action);

    /// <summary>Zmiana motywu (Jasny/Ciemny/Systemowy) — obsługuje aplikacja okienkowa.</summary>
    public event Action<string>? ThemeChanged;

    public void SetTheme(string theme)
    {
        Config.Theme = theme;
        SaveConfig();
        ThemeChanged?.Invoke(theme);
    }

    public DatabaseProfile Profile => Config.ActiveDatabase;
    public string DatabaseDescription => Profile.SafeDescription + (Profile.IsEmergencyWorkingCopy ? " — KOPIA ROBOCZA TRYBU AWARYJNEGO" : "");

    /// <summary>Kontekst bazy ograniczony do jednostki sesji (gdy wybrana) — dane innych jednostek są niewidoczne i niezmienialne.</summary>
    public KsiegiDbContext OpenDb()
    {
        if (Profile.Engine != DatabaseEngine.PostgreSql || _operatorSession is not { IsHealthy: true } active ||
            active.Identity != ExclusivePostgresSession.ProfileIdentity(Profile))
            throw new DomainException("Brak aktywnej, wyłącznej sesji PostgreSQL. Operacje księgowe są zablokowane.");
        var db = Connector.CreateContext(Profile);
        if (Company is { } c) db.CompanyScope = c.Id;
        return db;
    }

    public string AttachmentsRoot => Config.AttachmentsDirectory ?? Paths.AttachmentsDirectory;
    public LocalFileStore Attachments => new(AttachmentsRoot, TryAttachmentsKey(AttachmentsRoot), keyRequired: true);

    private static string AttachmentsSecretName(string attachmentsRoot) => "PelnaKsiegowosc/zalaczniki/" + Path.GetFullPath(attachmentsRoot).ToLowerInvariant();

    /// <summary>
    /// Klucz szyfrowania załączników dla katalogu magazynu (magazyn sekretów systemu). Przy bazie SQLite wyprowadzany z klucza bazy
    /// (HKDF-SHA256) — przeniesienie klucza bazy na inne stanowisko daje dostęp także do załączników; przy PostgreSQL — losowy
    /// (na kolejnym stanowisku ze wspólnym katalogiem — import klucza w Ustawieniach). Klucz niepasujący do plików już
    /// zaszyfrowanych w katalogu nie jest zapisywany ani używany (brak plików zaszyfrowanych różnymi kluczami).
    /// </summary>
    public byte[] AttachmentsKeyFor(string attachmentsRoot, DatabaseProfile? profile = null) =>
        TryAttachmentsKey(attachmentsRoot, profile)
        ?? throw new DomainException("Załączniki w katalogu zaszyfrowano innym kluczem — zaimportuj klucz załączników (Ustawienia).");

    public byte[]? TryAttachmentsKey(string attachmentsRoot, DatabaseProfile? profile = null)
    {
        var name = AttachmentsSecretName(attachmentsRoot);
        if (Secrets.Get(name) is { Length: > 0 } stored) return Convert.FromBase64String(stored);
        var dbKey = Connector.ExportSqliteKey(profile ?? Profile);
        var key = dbKey is { Length: 64 }
            ? System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, Convert.FromHexString(dbKey), 32,
                info: System.Text.Encoding.UTF8.GetBytes("PelnaKsiegowosc/zalaczniki"))
            : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        if (!LocalFileStore.KeyMatches(attachmentsRoot, key)) return null;
        Secrets.Set(name, Convert.ToBase64String(key));
        return key;
    }

    /// <summary>Klucz załączników do przeniesienia na inne stanowisko (Base64).</summary>
    public string? ExportAttachmentsKey() => Secrets.Get(AttachmentsSecretName(AttachmentsRoot));

    /// <summary>Import klucza załączników — tylko gdy otwiera pliki już zaszyfrowane w katalogu.</summary>
    public void ImportAttachmentsKey(string base64)
    {
        byte[] key;
        try { key = Convert.FromBase64String(base64.Trim()); } catch (FormatException) { throw new DomainException("Nieprawidłowy format klucza załączników."); }
        if (key.Length != 32) throw new DomainException("Klucz załączników musi mieć 256 bitów.");
        if (!LocalFileStore.KeyMatches(AttachmentsRoot, key)) throw new DomainException("Klucz nie otwiera załączników w katalogu tego stanowiska.");
        Secrets.Set(AttachmentsSecretName(AttachmentsRoot), Convert.ToBase64String(key));
    }
    /// <summary>Poświadczenia KSeF wg ustawień stanowiska: certyfikat .pfx (hasło z magazynu sekretów) albo token.</summary>
    public Infrastructure.Ksef.KsefCredentials KsefCredentials()
    {
        var k = Config.Ksef;
        if (k.UseCertificate)
        {
            if (string.IsNullOrWhiteSpace(k.CertificatePath) || !File.Exists(k.CertificatePath))
                throw new DomainException("Wskaż plik certyfikatu (.pfx/.p12) w sekcji Integracje → KSeF.");
            var password = Secrets.Get(k.CertificatePasswordSecretName);
            try
            {
                var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(k.CertificatePath, password);
                return new Infrastructure.Ksef.KsefCredentials(null, cert, k.CertificateByFingerprint);
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new DomainException($"Nie można otworzyć certyfikatu (złe hasło lub uszkodzony plik): {e.Message}");
            }
        }
        return Infrastructure.Ksef.KsefCredentials.FromToken(Secrets.Get(k.TokenSecretName) ?? throw new DomainException("Brak zapisanego tokenu KSeF dla wybranego środowiska (Integracje → KSeF)."));
    }

    public XsdValidator Validator => new(XsdValidator.BundledSchemasDirectory, Paths.SchemasDirectory);
    public string BackupDirectory => Config.Backup.LocalDirectory ?? Paths.DefaultBackupDirectory;

    public void SaveConfig() => Config.Save(Paths);

    /// <summary>Otwarcie aktywnej bazy: test połączenia, kopia przed migracją schematu (gdy baza zawiera dane), migracje, odczyt kontekstu.</summary>
    public async Task<OpenResult> OpenAsync(CancellationToken ct = default)
    {
        BackupService.CleanupStaleTemporaryData(Paths.TempDirectory); // pozostałości po poprzednim uruchomieniu
        try { await AcquireOperatorSessionAsync(ct); }
        catch (Exception ex) when (ex is DomainException or Npgsql.NpgsqlException or InvalidOperationException or IOException or TimeoutException)
        {
            return new OpenResult(false, ex is DomainException ? ex.Message : DatabaseConnector.Describe(ex), null);
        }
        var test = await Connector.TestAsync(Profile, ct);
        if (!test.Success)
            return new OpenResult(false, test.Message + (Profile.Engine == DatabaseEngine.PostgreSql
                ? " Program nie przełącza się automatycznie na inną bazę." : ""), null);
        string? backup = null, recoveryKey = null;
        // Wiele stanowisk nie może równocześnie wykonywać migracji schematu.
        // Uaktualnienia wymagają osobnej operacji administratora przy zatrzymanych klientach.
        if (test.PendingMigrations > 0)
            return new OpenResult(false, "Serwer ma nieaktualny schemat PostgreSQL. Administrator musi wykonać aktualizację z kopią bezpieczeństwa po zatrzymaniu innych stanowisk.", null);
        SaveConfig();
        await RefreshAsync(ct);
        await Attachments.EncryptLegacyFilesAsync(ct); // załączniki zapisane wcześniej jawnie — szyfrowane przy otwarciu
        return new OpenResult(true, null, backup, recoveryKey);
    }

    /// <summary>
    /// Kopia przed aktualizacją schematu — zawsze szyfrowana (AES-256-GCM, jak każda kopia programu). Gdy na stanowisku nie ma
    /// klucza kopii, program generuje nowy klucz, zapisuje go w magazynie sekretów systemu i zwraca klucz odzyskiwania do
    /// zanotowania. Niezaszyfrowana kopia pliku bazy nie jest tworzona.
    /// </summary>
    private async Task<(string File, string? RecoveryKey)> PreMigrationBackupAsync(CancellationToken ct)
    {
        string? recovery = null;
        if (StoredBackupKey() is null)
        {
            if (Profile.Engine != DatabaseEngine.Sqlite)
                throw new DomainException("Aktualizacja schematu bazy wymaga wcześniejszej kopii zapasowej, a klucz kopii nie jest zapisany na tym stanowisku. Zapisz klucz w sekcji „Backupy” albo wykonaj kopię serwera narzędziami PostgreSQL.");
            recovery = GenerateBackupKey();
        }
        var r = await BackupAsync("przed aktualizacją schematu bazy", upload: false, ct);
        return (r.FilePath, recovery);
    }

    /// <summary>Odczyt jednostki i lat obrotowych; wybór roku: dotychczas wybrany, pierwszy otwarty albo ostatni.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await using var db = OpenDb();
        Company = await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var authenticatedLogin = OperatorIdentity.LoginFor(Profile);
        User = Company is null
            ? OperatorIdentity.For(Guid.Empty, Profile)
            : OperatorIdentity.For(Company.Id, Profile,
                await new Application.Security.OperatorAccessService(db, Clock).ResolveAsync(
                    OperatorIdentity.IdFor(authenticatedLogin), authenticatedLogin, Company.Id, ct));
        Years = Company is null
            ? Array.Empty<FiscalYear>()
            : await db.FiscalYears.AsNoTracking().Include(f => f.Periods).Where(f => f.CompanyId == Company.Id).OrderBy(f => f.StartDate).ToListAsync(ct);
        foreach (var y in Years) y.Periods = y.Periods.OrderBy(p => p.Number).ToList();
        Year = Years.FirstOrDefault(y => y.Id == Year?.Id)
               ?? Years.FirstOrDefault(y => y.Status == FiscalYearStatus.Otwarty)
               ?? Years.LastOrDefault();
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectYear(Guid yearId)
    {
        Year = Years.FirstOrDefault(y => y.Id == yearId) ?? Year;
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AuditAsync(string action, string entityType, string? entityId, string? details, CancellationToken ct = default)
    {
        await using var db = OpenDb();
        await new AuditService(db, Clock).AddAsync(User, action, entityType, entityId, details, ct);
        await db.SaveChangesAsync(ct);
    }

    // ---- kopie zapasowe ----

    public BackupKey? StoredBackupKey()
    {
        var b64 = Secrets.Get(Config.Backup.KeySecretName);
        return string.IsNullOrEmpty(b64) ? null : new BackupKey.RawKey(Convert.FromBase64String(b64));
    }

    /// <summary>Zapisuje nowy losowy klucz kopii w magazynie sekretów i zwraca klucz odzyskiwania do zanotowania przez użytkownika.</summary>
    public string GenerateBackupKey()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Secrets.Set(Config.Backup.KeySecretName, Convert.ToBase64String(key));
        return ArchiveCrypto.FormatRecoveryKey(key);
    }

    public void StoreBackupKey(byte[] key) => Secrets.Set(Config.Backup.KeySecretName, Convert.ToBase64String(key));

    public S3BackupTarget? RemoteTarget() =>
        Config.ObjectStorage.Enabled ? S3BackupTarget.FromSecrets(Config.ObjectStorage, Secrets) : null;

    public async Task<BackupResult> BackupAsync(string reason, bool upload, CancellationToken ct = default)
    {
        var key = StoredBackupKey() ?? throw new DomainException("Brak klucza kopii zapasowych na tym stanowisku — wygeneruj go lub wprowadź klucz odzyskiwania w sekcji „Backupy”.");
        using var remote = upload && Config.Backup.UploadToObjectStorage ? RemoteTarget() : null;
        await using var db = OpenDb();
        var svc = new BackupService(Clock, Paths.TempDirectory);
        var result = await svc.CreateAsync(db, Profile, Attachments, Config.ToJsonWithoutSecrets(),
            new BackupOptions(key, BackupDirectory, Config.Backup.IncludeAttachments, reason, Config.Backup.RetainLocalCount), remote, ct);
        Config.Backup.LastSuccessUtc = Clock.UtcNow;
        Config.Backup.LastError = result.RemoteError;
        if (result.RemoteKey is not null) Config.ObjectStorage.LastSuccessUtc = Clock.UtcNow;
        SaveConfig();
        if (remote is not null && result.RemoteKey is not null && Config.ObjectStorage.RetainRemoteCount > 0)
            await remote.ApplyRetentionAsync(Config.ObjectStorage.RetainRemoteCount, ct);
        return result;
    }

    /// <summary>Czy należy wykonać kopię automatyczną (włączona, minął interwał, jest klucz).</summary>
    public bool AutomaticBackupDue() =>
        Config.Backup.AutomaticEnabled && StoredBackupKey() is not null && Company is not null &&
        (Config.Backup.LastSuccessUtc is not { } last || Clock.UtcNow - last >= TimeSpan.FromHours(Math.Max(1, Config.Backup.IntervalHours)));
}
