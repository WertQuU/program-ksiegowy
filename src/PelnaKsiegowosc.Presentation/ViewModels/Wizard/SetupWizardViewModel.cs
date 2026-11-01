using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Wizard;

public enum WizardStep
{
    Warunki, Baza, Jednostka, RokObrotowy, PlanKont, Polityka, OsobaOdpowiedzialna, KopieZapasowe, Ksef, Podsumowanie
}

/// <summary>
/// Kreator pierwszego uruchomienia. Kroki: akceptacja warunków licencji, centralny PostgreSQL, dane jednostki,
/// rok obrotowy, plan kont z szablonu, polityka rachunkowości, kierownik jednostki, klucz kopii zapasowych, utworzenie ksiąg.
/// Gdy wskazana baza zawiera już księgi (np. drugie stanowisko na serwerze), kreator tylko podłącza stanowisko.
/// </summary>
public sealed partial class SetupWizardViewModel : ViewModelBase
{
    public SetupWizardViewModel(AppSession session, IDialogService dialogs) : base(dialogs)
    {
        Session = session;
        TermsText = Terms.Text;
        Database.SqlitePath = session.Paths.DefaultDatabaseFile;
        // Nowa instalacja pracuje na wspólnym serwerze, po jednym koncie DB na operatora.
        Database.UsePostgres = !session.IsIsolatedSelfTest;
        LocalBackupDirectory = session.Config.Backup.LocalDirectory ?? session.Paths.DefaultBackupDirectory;
        var y = session.Clock.Today.Year;
        YearStart = new DateOnly(y, 1, 1).ToPl();
        YearEnd = new DateOnly(y, 12, 31).ToPl();
        ManagerRole = ManagerRoles[0];
    }

    public AppSession Session { get; }
    public string TermsText { get; }
    public DatabaseForm Database { get; } = new();
    public CompanyForm Company { get; } = new();

    /// <summary>Zakończono pomyślnie — okno kreatora zamyka się i otwiera się okno główne.</summary>
    public event EventHandler? Completed;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StepTitle), nameof(StepNumber), nameof(CanGoBack), nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(IsTerms), nameof(IsDatabase), nameof(IsCompany), nameof(IsYear), nameof(IsChart), nameof(IsPolicy), nameof(IsPerson), nameof(IsBackup), nameof(IsKsef), nameof(IsSummary))]
    private WizardStep _step = WizardStep.Warunki;

    public bool IsTerms => Step == WizardStep.Warunki;
    public bool IsDatabase => Step == WizardStep.Baza;
    public bool IsCompany => Step == WizardStep.Jednostka;
    public bool IsYear => Step == WizardStep.RokObrotowy;
    public bool IsChart => Step == WizardStep.PlanKont;
    public bool IsPolicy => Step == WizardStep.Polityka;
    public bool IsPerson => Step == WizardStep.OsobaOdpowiedzialna;
    public bool IsBackup => Step == WizardStep.KopieZapasowe;
    public bool IsKsef => Step == WizardStep.Ksef;
    public bool IsSummary => Step == WizardStep.Podsumowanie;

    public string StepNumber => $"Krok {(int)Step + 1} z {Enum.GetValues<WizardStep>().Length}";
    public string StepTitle => Step switch
    {
        WizardStep.Warunki => "Warunki licencji i odpowiedzialność",
        WizardStep.Baza => "Miejsce przechowywania ksiąg",
        WizardStep.Jednostka => "Dane jednostki",
        WizardStep.RokObrotowy => "Rok obrotowy",
        WizardStep.PlanKont => "Zakładowy plan kont",
        WizardStep.Polityka => "Polityka rachunkowości",
        WizardStep.OsobaOdpowiedzialna => "Kierownik jednostki",
        WizardStep.KopieZapasowe => "Kopie zapasowe",
        WizardStep.Ksef => "KSeF — poświadczenia tego stanowiska",
        _ => "Utworzenie ksiąg"
    };
    public bool CanGoBack => Step > WizardStep.Warunki && !Finished;
    public bool IsLastStep => Step == WizardStep.Podsumowanie;

    [ObservableProperty] private bool _termsAccepted;
    [ObservableProperty] private string _databaseInfo = "";
    [ObservableProperty] private bool _connectToExisting;
    [ObservableProperty] private bool _createFirstDatabase;
    [ObservableProperty] private string _administratorLogin = "postgres";
    [ObservableProperty] private string _administratorPassword = "";
    [ObservableProperty] private bool _companyStored;
    [ObservableProperty] private bool _yearStored;
    [ObservableProperty] private bool _chartStored;
    [ObservableProperty] private bool _policyStored;
    [ObservableProperty] private bool _managerStored;
    private Guid? _loadedCompanyId;
    private Guid? _loadedYearId;
    private Guid? _loadedManagerId;
    private string? _checkedConnection;
    private string? _loadedCompanySnapshot;
    private string? _storedChartName;

    [ObservableProperty] private string _yearStart = "";
    [ObservableProperty] private string _yearEnd = "";
    [ObservableProperty] private bool _isFirstYear = true;

    [ObservableProperty] private string _chartInfo = "";
    [ObservableProperty] private bool _approveChart = true;
    public ChartOptionsForm ChartOptions { get; } = new();

    [ObservableProperty] private string _policyContent = "";
    [ObservableProperty] private bool _approvePolicy;
    [ObservableProperty] private string _policyApprovedBy = "";

    [ObservableProperty] private string _managerFirstName = "";
    [ObservableProperty] private string _managerLastName = "";
    [ObservableProperty] private Choice<ResponsibleRole> _managerRole;
    public List<Choice<ResponsibleRole>> ManagerRoles { get; } = Choices.Of<ResponsibleRole>();

    [ObservableProperty] private string _recoveryKey = "";
    [ObservableProperty] private bool _recoveryKeySaved;
    [ObservableProperty] private bool _skipBackupKey;
    [ObservableProperty] private string _localBackupDirectory = "";
    [ObservableProperty] private bool _configureS3;
    [ObservableProperty] private string _s3Endpoint = "";
    [ObservableProperty] private string _s3Region = "";
    [ObservableProperty] private string _s3Bucket = "";
    [ObservableProperty] private string _s3AccessKey = "";
    [ObservableProperty] private string _s3SecretKey = "";

    // Sekret KSeF podaje operator stanowiska; trafia WYŁĄCZNIE do lokalnego magazynu sekretów.
    [ObservableProperty] private bool _configureKsef;
    [ObservableProperty] private string _ksefToken = "";
    [ObservableProperty] private Choice<KsefEnvironment> _ksefEnvironment = Choices.Of<KsefEnvironment>().First(x => x.Value == global::PelnaKsiegowosc.Infrastructure.Configuration.KsefEnvironment.Produkcja);
    public List<Choice<KsefEnvironment>> KsefEnvironments { get; } = Choices.Of<KsefEnvironment>();

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private TableData _readiness = TableData.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoBack))] private bool _finished;

    [RelayCommand]
    private void Back()
    {
        if (Step > WizardStep.Warunki) Step--;
    }

    [RelayCommand]
    private Task NextAsync() => RunAsync(async () =>
    {
        switch (Step)
        {
            case WizardStep.Warunki:
                if (!TermsAccepted) throw new DomainException("Aby korzystać z programu, zaakceptuj warunki licencji.");
                break;
            case WizardStep.Baza:
                if (CreateFirstDatabase) await PrepareFirstDatabaseCoreAsync();
                else await CheckDatabaseAsync();
                break;
            case WizardStep.Jednostka:
                var c = Company.ToCompany();
                c.Nip = Domain.Validation.Identifiers.NormalizeNip(c.Nip);
                var v = c.Validate();
                v.ThrowIfInvalid();
                if (v.Warnings.Count > 0 && !await Dialogs.ConfirmAsync("Dane jednostki", string.Join(Environment.NewLine, v.Warnings) + Environment.NewLine + Environment.NewLine + "Kontynuować?")) return;
                break;
            case WizardStep.RokObrotowy:
                FiscalYear.ValidateRange(Parse.Date(YearStart, "Początek roku"), Parse.Date(YearEnd, "Koniec roku"), IsFirstYear).ThrowIfInvalid();
                var form = Company.LegalForm.Value;
                if (!ChartStored) ChartInfo = $"Zostanie utworzony plan kont „{ChartTemplate.Name}” ({ChartTemplate.Accounts.Count} kont) — koszty w układzie rodzajowym, RZiS w wariancie porównawczym, " +
                            $"konto 801: „{LegalForms.ShareCapitalName(form)}”. Plan kont jest elementem polityki rachunkowości: możesz go zmienić w sekcji „Plan kont”.";
                break;
            case WizardStep.PlanKont:
                if (string.IsNullOrWhiteSpace(PolicyContent))
                {
                    var start = Parse.Date(YearStart, "Początek roku");
                    PolicyContent = PolicyTemplate.Build(Company.FullName, LegalForms.Name(Company.LegalForm.Value), LegalForms.ManagementBody(Company.LegalForm.Value),
                        start.Month == 1 ? "rok kalendarzowy" : $"od {start:dd.MM} do {Parse.Date(YearEnd, "Koniec roku"):dd.MM}", _storedChartName ?? ChartTemplate.Name, HelpVersion());
                }
                break;
            case WizardStep.Polityka:
                if (string.IsNullOrWhiteSpace(PolicyContent)) throw new DomainException("Podaj treść polityki rachunkowości.");
                if (ApprovePolicy && !PolicyStored)
                {
                    if (PolicyContent.Contains("[DO UZUPEŁNIENIA", StringComparison.OrdinalIgnoreCase))
                        throw new DomainException("Polityka zawiera pola [DO UZUPEŁNIENIA…] — uzupełnij je albo odznacz zatwierdzenie (zatwierdzisz później w sekcji „Jednostka i polityka”).");
                    if (string.IsNullOrWhiteSpace(PolicyApprovedBy)) throw new DomainException("Podaj osobę zatwierdzającą politykę (kierownik jednostki).");
                }
                break;
            case WizardStep.OsobaOdpowiedzialna:
                if (string.IsNullOrWhiteSpace(ManagerFirstName) || string.IsNullOrWhiteSpace(ManagerLastName))
                    throw new DomainException($"Podaj kierownika jednostki ({LegalForms.ManagementBody(Company.LegalForm.Value)}) — odpowiada za wykonywanie obowiązków w zakresie rachunkowości (art. 4 ust. 5 UoR).");
                break;
            case WizardStep.KopieZapasowe:
                if (string.IsNullOrWhiteSpace(LocalBackupDirectory))
                    throw new DomainException("Wskaż katalog lokalnych zaszyfrowanych kopii zapasowych.");
                if (ConfigureS3)
                {
                    if (!Uri.TryCreate(S3Endpoint.Trim(), UriKind.Absolute, out var endpoint) ||
                        endpoint.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(S3Region) ||
                        string.IsNullOrWhiteSpace(S3Bucket) ||
                        string.IsNullOrWhiteSpace(S3AccessKey) || string.IsNullOrWhiteSpace(S3SecretKey))
                        throw new DomainException("Kopie S3: podaj adres HTTPS endpointu, region, bucket oraz oba klucze dostępowe. Sekrety pozostaną w systemowym magazynie haseł tego komputera.");
                    if (!S3Bucket.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
                        throw new DomainException("Nazwa bucketu S3 zawiera niedozwolone znaki.");
                }
                if (!SkipBackupKey && string.IsNullOrEmpty(RecoveryKey) && Session.StoredBackupKey() is null)
                    throw new DomainException("Wygeneruj klucz kopii zapasowych albo zaznacz, że skonfigurujesz kopie później.");
                if (!string.IsNullOrEmpty(RecoveryKey) && !RecoveryKeySaved) throw new DomainException("Potwierdź, że zapisałeś klucz odzyskiwania poza komputerem.");
                break;
            case WizardStep.Ksef:
                if (ConfigureKsef && string.IsNullOrWhiteSpace(KsefToken))
                    throw new DomainException("Wpisz token KSeF tego stanowiska lub odznacz konfigurację i uzupełnij go później w Integracjach.");
                Summary = BuildSummary();
                break;
            case WizardStep.Podsumowanie:
                return;
        }
        Step++;
    });

    private static string HelpVersion() => Sections.HelpViewModel.Version;

    private string BuildSummary() => ConnectToExisting
        ? $"Stanowisko zostanie podłączone do istniejących ksiąg w bazie: {Database.ToProfile(Session.IsIsolatedSelfTest).SafeDescription}.{Environment.NewLine}" +
          $"Jednostka: {Company.FullName}, NIP {Company.Nip}.{Environment.NewLine}" +
          $"Rok obrotowy: {YearStart} – {YearEnd}. Plan kont: {_storedChartName ?? ChartTemplate.Name}.{Environment.NewLine}" +
          $"Kierownik jednostki: {ManagerFirstName} {ManagerLastName}. Brakujące dane zostaną zapisane przy zakończeniu kreatora.{Environment.NewLine}" +
          $"KSeF na stanowisku: {(ConfigureKsef ? $"token zapisany lokalnie ({KsefEnvironment.Value})" : "do konfiguracji w Integracjach")}. Kopie zapasowe: {(Session.StoredBackupKey() is null ? "do konfiguracji" : "klucz skonfigurowany")}."
        : $"Baza: {Database.ToProfile(Session.IsIsolatedSelfTest).SafeDescription}{Environment.NewLine}" +
          $"Jednostka: {Company.FullName} ({LegalForms.Name(Company.LegalForm.Value)}), NIP {Company.Nip}{Environment.NewLine}" +
          $"Rok obrotowy: {YearStart} – {YearEnd}{(IsFirstYear ? " (pierwszy rok działalności)" : "")}{Environment.NewLine}" +
          $"Plan kont: {ChartTemplate.Name}{(ApproveChart ? ", zatwierdzony" : ", do zatwierdzenia")}{Environment.NewLine}" +
          $"Polityka rachunkowości: {(ApprovePolicy ? $"zatwierdzona przez {PolicyApprovedBy}" : "do uzupełnienia i zatwierdzenia")}{Environment.NewLine}" +
          $"Kierownik jednostki: {ManagerFirstName} {ManagerLastName}{Environment.NewLine}" +
          $"Kopie zapasowe: {(Session.StoredBackupKey() is null ? "nieskonfigurowane" : "klucz zapisany, po utworzeniu ksiąg zostanie wykonana i zweryfikowana pierwsza kopia")}{Environment.NewLine}" +
          $"KSeF: {(ConfigureKsef ? $"token na tym stanowisku ({KsefEnvironment.Value})" : "do konfiguracji w Integracjach")}";

    private async Task CheckDatabaseAsync()
    {
        _checkedConnection = null;
        DatabaseInfo = "";
        Session.ReleaseOperatorSession();
        var p = Database.ToProfile(Session.IsIsolatedSelfTest);
        if (p.Engine != DatabaseEngine.PostgreSql && !(Session.IsIsolatedSelfTest && p.Engine == DatabaseEngine.Sqlite))
            throw new DomainException("Wspólne księgi wymagają jednej centralnej bazy PostgreSQL.");
        if (p.Engine == DatabaseEngine.Sqlite)
            Directory.CreateDirectory(Path.GetDirectoryName(p.SqlitePath!)!);
        if (p.Engine == DatabaseEngine.PostgreSql && !string.IsNullOrEmpty(Database.Password))
            Session.Connector.SavePassword(p, Database.Password);
        if (Session.Connector.SqliteKeyMissing(p))
        {
            var key = await Dialogs.PromptAsync("Klucz bazy", "Plik bazy jest zaszyfrowany (RODO). Wpisz klucz bazy — administrator wyświetli go na stanowisku, " +
                "na którym baza była używana (Ustawienia → „Pokaż klucz bazy”). Bez klucza możesz odtworzyć bazę z kopii zapasowej.", password: true);
            if (string.IsNullOrWhiteSpace(key)) throw new DomainException("Bez klucza nie można otworzyć zaszyfrowanej bazy.");
            Session.Connector.ImportSqliteKey(p, key);
        }
        await using var validationSession = p.Engine == DatabaseEngine.PostgreSql
            ? await ExclusivePostgresSession.AcquireAsync(p, Session.Connector.ConnectionString(p), requireOperatorRole: true)
            : null;
        var r = await Session.Connector.TestAsync(p);
        if (!r.Success) throw new DomainException(r.Message);
        if (p.Engine == DatabaseEngine.PostgreSql && r.PendingMigrations > 0)
            throw new DomainException("Baza nie ma aktualnego schematu. Dla pustej bazy wybierz w kroku 2 „Utwórz pierwszą bazę” i podaj osobne dane administratora. Aktualizacja istniejących ksiąg wymaga osobnej operacji administracyjnej.");
        await using var db = Session.Connector.CreateContext(p);
        var companies = r.DatabaseEmpty ? new List<Company>() : await db.Companies.AsNoTracking().Take(2).ToListAsync();
        if (companies.Count > 1) throw new DomainException("Baza zawiera kilka jednostek. Wskaż oddzielną bazę dla jednej jednostki.");
        var company = companies.SingleOrDefault();
        if (company is not null)
        {
            var login = OperatorIdentity.LoginFor(p);
            await new Application.Security.OperatorAccessService(db, Session.Clock).ResolveAsync(
                OperatorIdentity.IdFor(login), login, company.Id);
        }
        await LoadStoredDataAsync(db, company);
        ConnectToExisting = company is not null;
        DatabaseInfo = r.Message + (ConnectToExisting
            ? " Pobrano dane jednostki i ksiąg. Kolejne kroki pokażą zapisane informacje; uzupełnij brakujące dane."
            : " Baza nie zawiera jednostki. Podaj dane w kolejnych krokach.");
        Session.Config.ActiveDatabase = p;
        _checkedConnection = p.BuildConnectionString(null);
    }

    private async Task LoadStoredDataAsync(KsiegiDbContext db, Company? company)
    {
        ChartOptions.DetailedCosts = true; ChartOptions.TradingGoods = true;
        ChartOptions.Employees = false; ChartOptions.ForeignCurrencyEur = false;
        ChartOptions.ForeignCurrencyUsd = false; ChartOptions.BankLoans = false; ChartOptions.Leasing = false;
        Company.Load(company ?? new Company());
        _loadedCompanyId = company?.Id;
        _loadedCompanySnapshot = company is null ? null : System.Text.Json.JsonSerializer.Serialize(company);
        CompanyStored = company?.Validate().IsValid == true;
        var year = company is null ? null : await db.FiscalYears.AsNoTracking()
            .Where(y => y.CompanyId == company.Id)
            .OrderBy(y => y.Status == FiscalYearStatus.Otwarty ? 0 : 1)
            .ThenByDescending(y => y.StartDate).FirstOrDefaultAsync();
        _loadedYearId = year?.Id;
        YearStored = year is not null;
        YearStart = (year?.StartDate ?? new DateOnly(Session.Clock.Today.Year, 1, 1)).ToPl();
        YearEnd = (year?.EndDate ?? new DateOnly(Session.Clock.Today.Year, 12, 31)).ToPl();
        IsFirstYear = year?.IsFirstYear ?? true;
        var chart = year is null ? null : await db.Charts.AsNoTracking()
            .Where(c => c.FiscalYearId == year.Id).OrderByDescending(c => c.Version).FirstOrDefaultAsync();
        ChartStored = chart is not null;
        _storedChartName = chart?.Name;
        ApproveChart = chart?.IsApproved ?? true;
        ChartInfo = chart is null ? "" : $"Pobrano zapisany plan kont: {chart.Name}, wersja {chart.Version}. Zostanie zachowany.";
        var policy = company is null ? null : await db.AccountingPolicies.AsNoTracking()
            .Where(p => p.CompanyId == company.Id).OrderByDescending(p => p.Version).FirstOrDefaultAsync();
        PolicyStored = !string.IsNullOrWhiteSpace(policy?.Content);
        PolicyContent = policy?.Content ?? "";
        ApprovePolicy = policy?.ApprovedByManagement ?? false;
        PolicyApprovedBy = policy?.ApprovedBy ?? "";
        var person = company is null ? null : await db.ResponsiblePersons.AsNoTracking()
            .Where(p => p.CompanyId == company.Id && p.SignsFinancialStatements &&
                (p.From == null || p.From <= Session.Clock.Today) && (p.To == null || p.To >= Session.Clock.Today))
            .OrderBy(p => p.Role == ResponsibleRole.GlownyKsiegowy || p.Role == ResponsibleRole.OsobaProwadzacaKsiegi || p.Role == ResponsibleRole.BiuroRachunkowe ? 1 : 0)
            .ThenBy(p => p.Id).FirstOrDefaultAsync();
        _loadedManagerId = person?.Id;
        ManagerStored = !string.IsNullOrWhiteSpace(person?.FirstName) && !string.IsNullOrWhiteSpace(person?.LastName);
        ManagerFirstName = person?.FirstName ?? "";
        ManagerLastName = person?.LastName ?? "";
        ManagerRole = ManagerRoles.First(x => x.Value == (person?.Role ?? ResponsibleRole.CzlonekZarzadu));
    }

    [RelayCommand]
    private Task TestDatabaseAsync() => RunAsync(CheckDatabaseAsync);

    [RelayCommand]
    private Task PrepareFirstDatabaseAsync() => RunAsync(PrepareFirstDatabaseCoreAsync);

    private async Task PrepareFirstDatabaseCoreAsync()
    {
        _checkedConnection = null;
        Session.ReleaseOperatorSession();
        var profile = Database.ToProfile(Session.IsIsolatedSelfTest);
        var operatorPassword = string.IsNullOrEmpty(Database.Password)
            ? Session.Secrets.Get(profile.PasswordSecretKey) ?? "" : Database.Password;
        try
        {
            await PostgresFirstSetup.PrepareAsync(profile, AdministratorLogin.Trim(), AdministratorPassword, operatorPassword);
        }
        finally { AdministratorPassword = ""; }
        CreateFirstDatabase = false;
        await CheckDatabaseAsync();
    }

    [RelayCommand]
    private async Task PickSqliteAsync()
    {
        if (await Dialogs.SaveFileAsync("Plik bazy danych", "ksiegi.db", new FileFilter("Baza SQLite", "*.db")) is { } f) Database.SqlitePath = f;
    }

    [RelayCommand]
    private Task GenerateKeyAsync() => RunAsync(() =>
    {
        RecoveryKey = Session.GenerateBackupKey();
        RecoveryKeySaved = false;
        SkipBackupKey = false;
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task EnterKeyAsync() => RunAsync(async () =>
    {
        var text = await Dialogs.PromptAsync("Klucz odzyskiwania", "Wpisz posiadany klucz odzyskiwania kopii (np. z innego stanowiska):", password: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        Session.StoreBackupKey(Sections.BackupsViewModel.TryParseRecoveryKey(text) ?? throw new DomainException("Nieprawidłowy klucz odzyskiwania."));
        RecoveryKey = "";
        SkipBackupKey = false;
        StatusMessage = "Zapisano klucz kopii na tym stanowisku.";
    });

    [RelayCommand]
    private Task FinishAsync() => RunAsync(async () =>
    {
        if (Finished) { Completed?.Invoke(this, EventArgs.Empty); return; }
        var profile = Session.Config.ActiveDatabase;
        if (_checkedConnection is null || _checkedConnection != Database.ToProfile(Session.IsIsolatedSelfTest).BuildConnectionString(null)
            || _checkedConnection != profile.BuildConnectionString(null))
            throw new DomainException("Sprawdź połączenie i pobierz dane w kroku 2 przed zakończeniem kreatora.");
        await Session.AcquireOperatorSessionAsync();
        await using (var db = Session.OpenDb())
        {
            // A PostgreSQL operator cannot run migrations, even when all migrations
            // are already applied (EF may still attempt DDL for its history table).
            if (profile.Engine == DatabaseEngine.PostgreSql &&
                (await db.Database.GetPendingMigrationsAsync()).Any())
                throw new DomainException("Wymagana aktualizacja schematu PostgreSQL. Administrator musi przygotować schemat osobno przed zakończeniem kreatora. Konto operatora nie wykonuje migracji.");
            if (!ConnectToExisting && Session.IsIsolatedSelfTest && profile.Engine == DatabaseEngine.Sqlite)
                await db.Database.MigrateAsync();
            if (ConnectToExisting)
            {
                var id = await db.Companies.Select(c => c.Id).FirstOrDefaultAsync();
                if (id == Guid.Empty)
                    throw new DomainException("W bazie brakuje ksiąg. Sprawdź konfigurację serwera.");
                var existingLogin = OperatorIdentity.LoginFor(profile);
                await new Application.Security.OperatorAccessService(db, Session.Clock).ResolveAsync(
                    OperatorIdentity.IdFor(existingLogin), existingLogin, id);
                if (id != _loadedCompanyId) throw new DomainException("Dane jednostki zmieniły się. Wróć do kroku 2 i pobierz je ponownie.");
            }
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            if (!ConnectToExisting)
            {
                if (await db.Companies.AnyAsync()) throw new DomainException("Baza zawiera już księgi — wróć do kroku wyboru bazy.");
                await CreateBooksAsync(db);
            }
            else await CompleteStoredBooksAsync(db);
            var companyId = await db.Companies.Select(c => c.Id).FirstAsync();
            var login = OperatorIdentity.LoginFor(Session.Profile);
            var assignedRoles = await new Application.Security.OperatorAccessService(db, Session.Clock).ResolveAsync(
                OperatorIdentity.IdFor(login), login, companyId);
            var actor = OperatorIdentity.For(companyId, Session.Profile, assignedRoles);
            var settings = new SettingsService(db, Session.Clock);
            await settings.SetAsync(null, $"{Terms.SettingKey}.{actor.Login}", new Terms.Acceptance(Terms.Version, actor.Login, Session.Clock.UtcNow));
            await new AuditService(db, Session.Clock).AddAsync(actor, "WARUNKI_ZAAKCEPTOWANE", "Terms", Terms.Version, $"Licencja w wersji {Terms.Version}; stanowisko {Environment.MachineName}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        // Lokalna/zdalna konfiguracja kopii tego stanowiska; sekrety S3 zostają w magazynie OS.
        var backupDir = Path.GetFullPath(LocalBackupDirectory.Trim());
        Directory.CreateDirectory(backupDir);
        Session.Config.Backup.LocalDirectory = backupDir;
        if (ConfigureS3)
        {
            var o = Session.Config.ObjectStorage;
            o.Enabled = true;
            o.Endpoint = S3Endpoint.Trim();
            o.Region = S3Region.Trim();
            o.Bucket = S3Bucket.Trim();
            Session.Secrets.Set(o.AccessKeySecretName, S3AccessKey.Trim());
            Session.Secrets.Set(o.SecretKeySecretName, S3SecretKey.Trim());
            Session.Config.Backup.UploadToObjectStorage = true;
            S3AccessKey = S3SecretKey = "";
        }
        // Poświadczenia KSeF są ustawieniem KOMPUTERA, a nie wspólnej bazy; nigdy nie trafiają do audytu, ksiąg ani JSON.
        if (ConfigureKsef)
        {
            Session.Config.Ksef.Environment = KsefEnvironment.Value;
            Session.Secrets.Set(Session.Config.Ksef.TokenSecretName, KsefToken.Trim());
            Session.Config.Ksef.Enabled = true;
            KsefToken = "";
        }
        Session.Config.TermsAcceptedVersion = Terms.Version;
        Session.Config.TermsAcceptedUtc = Session.Clock.UtcNow;
        Session.Config.SetupCompleted = true;
        Session.SaveConfig();
        await Session.RefreshAsync();

        var result = "Księgi są gotowe.";
        if (Session.StoredBackupKey() is not null)
        {
            try
            {
                var b = await Session.BackupAsync("pierwsza kopia (kreator)", upload: false);
                result += $" Wykonano i zweryfikowano pierwszą kopię zapasową: {b.FilePath}";
            }
            catch (Exception ex) when (ex is DomainException or IOException)
            {
                result += " Pierwsza kopia zapasowa nie powiodła się: " + ex.Message;
            }
        }
        await using (var db = Session.OpenDb())
        {
            var t = new TableData(new TableColumn("Wymaganie", Width: 2), new TableColumn("Stan", Width: 0.6), new TableColumn("Szczegóły", Width: 3));
            foreach (var (item, ok, details) in await new SetupService(db, Session.Clock).ReadinessAsync(Session.CompanyId))
                t.Add(null, item, ok ? "✔" : "✖", details);
            Readiness = t;
        }
        Summary = result + Environment.NewLine + "Pozycje oznaczone ✖ uzupełnisz w programie (Pulpit pokazuje listę gotowości). Kliknij „Zakończ”, aby przejść do programu.";
        Finished = true;
    });

    private async Task CompleteStoredBooksAsync(KsiegiDbContext db)
    {
        var company = await db.Companies.SingleAsync(c => c.Id == _loadedCompanyId);
        var login = OperatorIdentity.LoginFor(Session.Profile);
        var roles = await new Application.Security.OperatorAccessService(db, Session.Clock).ResolveAsync(
            OperatorIdentity.IdFor(login), login, company.Id);
        var actor = OperatorIdentity.For(company.Id, Session.Profile, roles);
        var setup = new SetupService(db, Session.Clock);
        if (!CompanyStored)
        {
            if (System.Text.Json.JsonSerializer.Serialize(company) != _loadedCompanySnapshot)
                throw new DomainException("Dane jednostki zmieniły się. Wróć do kroku 2 i pobierz je ponownie.");
            await setup.UpdateCompanyAsync(actor, Company.ToCompany(company));
        }
        var year = _loadedYearId is { } yearId
            ? await db.FiscalYears.SingleAsync(y => y.Id == yearId && y.CompanyId == company.Id)
            : await setup.CreateFiscalYearAsync(actor, company.Id, Parse.Date(YearStart, "Początek roku"), Parse.Date(YearEnd, "Koniec roku"), IsFirstYear);
        if (!await db.Charts.AnyAsync(c => c.FiscalYearId == year.Id))
        {
            var chart = await new ChartService(db, actor, Session.Clock).CreateFromTemplateAsync(year.Id, ChartTemplate.Build(company.LegalForm, ChartOptions.ToOptions()));
            await ChartOptions.ApplyPostingSettingsAsync(db, company.Id, Session.Clock);
            if (ApproveChart) await setup.ApproveChartAsync(actor, chart.Id);
        }
        if (!PolicyStored)
        {
            var policy = await db.AccountingPolicies.OrderByDescending(p => p.Version).FirstOrDefaultAsync(p => p.CompanyId == company.Id);
            if (!string.IsNullOrWhiteSpace(policy?.Content))
                throw new DomainException("Polityka została uzupełniona na innym stanowisku. Wróć do kroku 2 i pobierz dane ponownie.");
            policy ??= await setup.CreatePolicyFromTemplateAsync(actor, company.Id);
            policy = await setup.SavePolicyAsync(actor, policy.Id, PolicyContent, policy.CorrectionMethod, policy.LowValueAssetThreshold, policy.IsSmallEntity, policy.IsMicroEntity);
            if (ApprovePolicy) await setup.ApprovePolicyAsync(actor, policy.Id, PolicyApprovedBy.Trim());
        }
        if (!ManagerStored)
        {
            actor.Require(Domain.Security.Permission.ManageCompany);
            var person = _loadedManagerId is { } personId
                ? await db.ResponsiblePersons.SingleAsync(p => p.Id == personId && p.CompanyId == company.Id)
                : new ResponsiblePerson { CompanyId = company.Id, SignsFinancialStatements = true, From = year.StartDate };
            if (_loadedManagerId is null) db.ResponsiblePersons.Add(person);
            else if (!string.IsNullOrWhiteSpace(person.FirstName) && !string.IsNullOrWhiteSpace(person.LastName))
                throw new DomainException("Dane osoby odpowiedzialnej zmieniły się. Wróć do kroku 2 i pobierz je ponownie.");
            person.FirstName = ManagerFirstName.Trim(); person.LastName = ManagerLastName.Trim(); person.Role = ManagerRole.Value;
            await new AuditService(db, Session.Clock).AddAsync(actor, "OSOBA_ODPOWIEDZIALNA_UZUPELNIONA", "ResponsiblePerson", person.Id.ToString(), "Uzupełnienie danych w kreatorze stanowiska");
            await db.SaveChangesAsync();
        }
    }

    private async Task CreateBooksAsync(KsiegiDbContext db)
    {
        var setup = new SetupService(db, Session.Clock);
        var company = await setup.CreateCompanyAsync(OperatorIdentity.For(Guid.Empty, Session.Profile), Company.ToCompany());
        var actor = OperatorIdentity.For(company.Id, Session.Profile);
        // Administrator powstaje TYLKO przy utworzeniu nowych ksiąg, nigdy przy pierwszym
        // połączeniu z istniejącą bazą (zabezpieczenie przed przejęciem roli przez obcego klienta).
        var founder = new Domain.Security.User
        {
            Id = actor.UserId, Login = actor.Login, DisplayName = actor.Login,
            PasswordAlgorithm = Application.Security.OperatorAccessService.SystemAccount,
            IsActive = true, CreatedAtUtc = Session.Clock.UtcNow
        };
        founder.SetRoles(Application.Security.OperatorAccessService.FounderRoles);
        db.Users.Add(founder);
        await new AuditService(db, Session.Clock).AddAsync(actor, "OPERATOR_ZALOZYCIEL", "User",
            founder.Id.ToString(), $"{founder.Login}; role={founder.RolesCsv}");
        await db.SaveChangesAsync();
        var year = await setup.CreateFiscalYearAsync(actor, company.Id, Parse.Date(YearStart, "Początek roku"), Parse.Date(YearEnd, "Koniec roku"), IsFirstYear);
        var chart = await new ChartService(db, actor, Session.Clock).CreateFromTemplateAsync(year.Id, ChartTemplate.Build(company.LegalForm, ChartOptions.ToOptions()));
        await ChartOptions.ApplyPostingSettingsAsync(db, company.Id, Session.Clock);
        if (ApproveChart) await setup.ApproveChartAsync(actor, chart.Id);
        var policy = await setup.CreatePolicyFromTemplateAsync(actor, company.Id);
        policy = await setup.SavePolicyAsync(actor, policy.Id, PolicyContent, CorrectionMethod.StornoCzerwone, 10000m, false, false);
        if (ApprovePolicy) await setup.ApprovePolicyAsync(actor, policy.Id, PolicyApprovedBy.Trim());
        var bank = new BankService(db, actor, Session.Clock);
        var posting = new PostingSettings();
        await bank.CreateMoneyAccountAsync(MoneyAccountKind.RachunekBankowy, "Rachunek bieżący", posting.Bank, company.DefaultBankAccountIban);
        await bank.CreateMoneyAccountAsync(MoneyAccountKind.Kasa, "Kasa główna", posting.Cash, null);
        db.ResponsiblePersons.Add(new ResponsiblePerson
        {
            CompanyId = company.Id, FirstName = ManagerFirstName.Trim(), LastName = ManagerLastName.Trim(), Role = ManagerRole.Value,
            SignsFinancialStatements = true, From = year.StartDate
        });
        await db.SaveChangesAsync();
    }
}
