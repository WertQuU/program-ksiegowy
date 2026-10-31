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

    public KsiegiDbContext OpenDb() => Connector.CreateContext(Profile);

    public string AttachmentsRoot => Config.AttachmentsDirectory ?? Paths.AttachmentsDirectory;
    public LocalFileStore Attachments => new(AttachmentsRoot);
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
        BackupService.CleanupStaleTemporaryData(Paths.TempDirectory); // pozostałości jawnych danych po przerwanej kopii / weryfikacji
        var test = await Connector.TestAsync(Profile, ct);
        if (!test.Success)
            return new OpenResult(false, test.Message + (Profile.Engine == DatabaseEngine.PostgreSql
                ? " Program nie przełącza się automatycznie na inną bazę." : ""), null);
        string? backup = null, recoveryKey = null;
        if (test.PendingMigrations > 0 && !test.DatabaseEmpty)
            (backup, recoveryKey) = await PreMigrationBackupAsync(ct);
        await using (var db = OpenDb())
            await db.Database.MigrateAsync(ct);
        SaveConfig();
        await RefreshAsync(ct);
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
        User = Company is null
            ? OperatorIdentity.For(Guid.Empty)
            : OperatorIdentity.For(Company.Id, await new Application.Security.OperatorAccessService(db, Clock).ResolveAsync(OperatorIdentity.IdFor(OperatorIdentity.Login), OperatorIdentity.Login, Company.Id, ct));
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
