using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Migration;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Ustawienia stanowiska: baza danych (lokalna/serwer), migracja SQLite → PostgreSQL, katalog załączników.</summary>
public sealed partial class SettingsViewModel : SectionViewModel
{
    public SettingsViewModel(AppSession s, IDialogService d) : base(s, d, "Ustawienia", "⚙") { }

    public DatabaseForm Database { get; } = new();
    [ObservableProperty] private string _activeInfo = "";
    [ObservableProperty] private string _attachmentsDirectory = "";
    [ObservableProperty] private string _pathsInfo = "";
    [ObservableProperty] private List<Choice<int>> _otherProfiles = new();
    [ObservableProperty] private Choice<int>? _otherProfile;
    public List<string> Themes { get; } = new() { "Jasny", "Ciemny", "Systemowy" };
    [ObservableProperty] private string _theme = "Jasny";
    private bool _loadingTheme;

    partial void OnThemeChanged(string value)
    {
        if (!_loadingTheme && value != Session.Config.Theme) Session.SetTheme(value);
    }

    /// <summary>Stan oficjalnych schematów XSD (dostępny / brak) — do walidacji JPK, e-Faktur i e-Sprawozdań.</summary>
    [ObservableProperty] private List<SchemaItem> _schemas = new();
    [ObservableProperty] private SchemaItem? _selectedSchema;

    private Infrastructure.Xml.SchemaLibrary Library(HttpClient? http = null) =>
        new(Session.Paths.SchemasDirectory, http, Infrastructure.Xml.XsdValidator.BundledSchemasDirectory);

    private void RefreshSchemas() =>
        Schemas = Library().Status().Select(x => new SchemaItem(x.Schema.Name, x.Available ? "✔ dostępny" : "✖ brak — plik nie zostanie zwalidowany", x.Schema.Purpose, x.Schema.InfoUrl, x.Schema.DownloadUrl is not null, x.Schema)).ToList();

    /// <summary>Klucz szyfrowania pliku bazy SQLite — do przeniesienia bazy na inne stanowisko (tylko administrator).</summary>
    [RelayCommand]
    private Task ShowDatabaseKeyAsync() => RunAsync(async () =>
    {
        Application.Abstractions.PermissionGuard.Require(Session.User, Domain.Security.Permission.ManageUsers);
        var key = Session.Connector.ExportSqliteKey(Session.Profile) ?? throw new DomainException("Aktywna baza nie jest plikiem SQLite szyfrowanym na tym stanowisku.");
        if (!await Dialogs.ConfirmAsync("Klucz bazy", "Klucz otwiera plik bazy z danymi księgowymi i osobowymi. Wyświetlić? Przekaż go wyłącznie bezpiecznym kanałem.")) return;
        await Session.AuditAsync("KLUCZ_BAZY_WYSWIETLONY", "Database", null, Session.Profile.SafeDescription);
        await Dialogs.ShowTextAsync("Klucz bazy (SQLCipher, AES-256)", key);
    });

    [RelayCommand]
    private Task DownloadSchemasAsync() => RunAsync(async () =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var lib = Library(http);
        var log = new List<string>();
        foreach (var st in lib.Status().Where(x => !x.Available && x.Schema.DownloadUrl is not null))
        {
            try
            {
                var files = await lib.DownloadAsync(st.Schema);
                log.Add($"✔ {st.Schema.Name}: pobrano {files.Count} plik(i)");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Xml.XmlException)
            {
                log.Add($"✖ {st.Schema.Name}: {e.Message} — pobierz ręcznie ze strony {st.Schema.InfoUrl}");
            }
        }
        foreach (var st in lib.Status().Where(x => !x.Available && x.Schema.DownloadUrl is null))
            log.Add($"• {st.Schema.Name}: pobierz ze strony {st.Schema.InfoUrl} i użyj „Wczytaj z pliku…”");
        if (log.Count == 0) log.Add("Wszystkie znane schematy są już dostępne.");
        var missing = lib.MissingImports();
        if (missing.Count > 0) log.Add("Brakujące schematy importowane: " + string.Join(", ", missing));
        RefreshSchemas();
        await Session.AuditAsync("SCHEMATY_POBRANE", "Schema", null, string.Join("; ", log));
        await Dialogs.ShowTextAsync("Schematy XSD", string.Join(Environment.NewLine, log));
    });

    [RelayCommand]
    private Task ImportSchemaAsync() => RunAsync(async () =>
    {
        var f = await Dialogs.OpenFileAsync("Schemat XSD lub archiwum ZIP ze strony MF", new FileFilter("Schemat XSD / ZIP", "*.xsd", "*.zip"));
        if (f is null) return;
        var lib = Library();
        List<string> saved;
        try { saved = lib.Import(new[] { f }); }
        catch (InvalidOperationException e) { throw new DomainException(e.Message); }
        var missing = lib.MissingImports();
        RefreshSchemas();
        await Session.AuditAsync("SCHEMAT_WCZYTANY", "Schema", null, string.Join(", ", saved));
        await Dialogs.InfoAsync("Schematy XSD", $"Wczytano: {string.Join(", ", saved)}" +
            (missing.Count > 0 ? $"{Environment.NewLine}Brakuje jeszcze schematów importowanych: {string.Join(", ", missing)} — wczytaj je lub użyj „Pobierz z serwera MF”." : ""));
    });

    [RelayCommand]
    private void OpenSchemaPage()
    {
        if (SelectedSchema is { } s) Dialogs.OpenInSystem(s.InfoUrl);
    }

    protected override Task LoadCoreAsync()
    {
        RefreshSchemas();
        var p = Session.Profile;
        ActiveInfo = $"Aktywna baza: {Session.DatabaseDescription}" + (p.Engine == DatabaseEngine.PostgreSql ? $" (rola: {Display.Enum(p.Role)})" : "") +
                     ". Program nie przełącza się samoczynnie między bazami — zmiana wymaga Twojej decyzji i ponownego uruchomienia.";
        Database.Load(p);
        _loadingTheme = true;
        Theme = Themes.Contains(Session.Config.Theme) ? Session.Config.Theme : "Jasny";
        _loadingTheme = false;
        AttachmentsDirectory = Session.AttachmentsRoot;
        OtherProfiles = Session.Config.OtherProfiles.Select((x, i) => new Choice<int>(i, x.SafeDescription)).ToList();
        PathsInfo = $"Katalog danych: {Session.Paths.Root}{Environment.NewLine}Konfiguracja: {Session.Paths.ConfigFile}{Environment.NewLine}" +
                    $"Kopie: {Session.BackupDirectory}{Environment.NewLine}Logi: {Session.Paths.LogsDirectory}{Environment.NewLine}Sekrety: {Session.Secrets.ProviderName}" +
                    (Session.Config.TermsAcceptedVersion is { } v ? $"{Environment.NewLine}Warunki licencji zaakceptowane: wersja {v}, {Session.Config.TermsAcceptedUtc?.ToPlDateTime()}" : "");
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PickSqliteAsync()
    {
        if (await Dialogs.SaveFileAsync("Plik bazy danych", "ksiegi.db", new FileFilter("Baza SQLite", "*.db")) is { } f) Database.SqlitePath = f;
    }

    [RelayCommand]
    private async Task PickCertificateAsync()
    {
        if (await Dialogs.OpenFileAsync("Certyfikat CA serwera", new FileFilter("Certyfikat", "*.crt", "*.pem", "*.cer")) is { } f) Database.RootCertificate = f;
    }

    private DatabaseProfile ProfileWithPassword()
    {
        var p = Database.ToProfile();
        if (p.Engine == DatabaseEngine.PostgreSql && !string.IsNullOrEmpty(Database.Password)) Session.Connector.SavePassword(p, Database.Password);
        return p;
    }

    [RelayCommand]
    private Task TestDatabaseAsync() => RunAsync(async () =>
    {
        var r = await Session.Connector.TestAsync(ProfileWithPassword());
        await Dialogs.InfoAsync("Test połączenia", r.Message + (r.Success ? $"{Environment.NewLine}Baza {(r.DatabaseEmpty ? "jest pusta" : "zawiera dane")}." : ""));
    });

    [RelayCommand]
    private Task UseDatabaseAsync() => RunAsync(async () =>
    {
        var p = ProfileWithPassword();
        var r = await Session.Connector.TestAsync(p);
        if (!r.Success) throw new DomainException(r.Message);
        if (!await Dialogs.ConfirmAsync("Zmiana bazy", $"Przełączyć program na: {p.SafeDescription}?{Environment.NewLine}" +
                (r.DatabaseEmpty ? "Baza jest pusta — po uruchomieniu zostanie uruchomiony kreator. Aby przenieść obecne księgi, użyj „Migruj dane do tej bazy”." : "Baza zawiera dane.") +
                $"{Environment.NewLine}Bieżąca baza pozostanie bez zmian. Wymagane ponowne uruchomienie programu.")) return;
        Session.Config.OtherProfiles.Add(Session.Config.ActiveDatabase);
        Session.Config.ActiveDatabase = p;
        if (r.DatabaseEmpty) Session.Config.SetupCompleted = false;
        Session.SaveConfig();
        await Dialogs.InfoAsync("Zmiana bazy", "Zapisano. Zamknij i uruchom ponownie program.");
    });

    [RelayCommand]
    private Task RestoreOtherProfileAsync() => RunAsync(async () =>
    {
        if (OtherProfile is null) throw new DomainException("Wybierz profil z listy.");
        var p = Session.Config.OtherProfiles[OtherProfile.Value];
        if (!await Dialogs.ConfirmAsync("Zmiana bazy", $"Wrócić do bazy: {p.SafeDescription}? Wymagane ponowne uruchomienie.")) return;
        Session.Config.OtherProfiles.RemoveAt(OtherProfile.Value);
        Session.Config.OtherProfiles.Add(Session.Config.ActiveDatabase);
        Session.Config.ActiveDatabase = p;
        Session.SaveConfig();
        await Dialogs.InfoAsync("Zmiana bazy", "Zapisano. Zamknij i uruchom ponownie program.");
    });

    [RelayCommand]
    private Task MigrateAsync() => RunAsync(async () =>
    {
        var target = ProfileWithPassword();
        if (target.Engine == Session.Profile.Engine && target.SafeDescription == Session.Profile.SafeDescription) throw new DomainException("Wskaż inną bazę docelową niż aktywna.");
        var test = await Session.Connector.TestAsync(target);
        if (!test.Success) throw new DomainException(test.Message);
        if (!test.DatabaseEmpty) throw new DomainException("Baza docelowa zawiera dane — migracja wymaga pustej bazy.");
        if (!await Dialogs.ConfirmAsync("Migracja danych", $"Przenieść wszystkie dane z {Session.DatabaseDescription} do {target.SafeDescription}?{Environment.NewLine}" +
                "Przed migracją zostanie wykonana kopia zapasowa. Baza źródłowa nie zostanie zmieniona. Po migracji porównywane są liczby rekordów, obroty i skróty zapisów.")) return;
        await using var source = Session.OpenDb();
        await using var dest = Session.Connector.CreateContext(target);
        var report = await new DatabaseMigrator(Session.Clock, Session.Paths.TempDirectory).MigrateAsync(source, Session.DatabaseDescription, Session.Attachments,
            dest, target.SafeDescription, Session.Attachments, async () => (await Session.BackupAsync("przed migracją bazy", upload: true)).FilePath);
        var reportFile = Path.Combine(Session.Paths.LogsDirectory, $"migracja-{Session.Clock.UtcNow:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(reportFile, report.ToText());
        await Session.AuditAsync(report.Success ? "MIGRACJA_ZAKONCZONA" : "MIGRACJA_NIEUDANA", "Database", null, $"{Session.DatabaseDescription} → {target.SafeDescription}");
        await Dialogs.ShowTextAsync("Raport migracji", report.ToText() + $"{Environment.NewLine}{Environment.NewLine}Raport zapisano: {reportFile}");
        if (report.Success && await Dialogs.ConfirmAsync("Migracja", "Migracja zakończona powodzeniem. Przełączyć program na nową bazę (po ponownym uruchomieniu)?"))
        {
            Session.Config.OtherProfiles.Add(Session.Config.ActiveDatabase);
            Session.Config.ActiveDatabase = target;
            Session.SaveConfig();
            await Dialogs.InfoAsync("Migracja", "Zapisano. Zamknij i uruchom ponownie program.");
        }
    });

    [RelayCommand]
    private Task SaveAttachmentsDirectoryAsync() => ChangeAsync(async () =>
    {
        var dir = Path.GetFullPath(AttachmentsDirectory.Trim());
        if (dir == Session.AttachmentsRoot) return;
        if (Directory.Exists(Session.AttachmentsRoot) && Directory.EnumerateFiles(Session.AttachmentsRoot, "*", SearchOption.AllDirectories).Any() &&
            !await Dialogs.ConfirmAsync("Katalog załączników", "Bieżący katalog zawiera pliki — przenieś je ręcznie do nowej lokalizacji z zachowaniem struktury podkatalogów. Kontynuować?")) return;
        Directory.CreateDirectory(dir);
        Session.Config.AttachmentsDirectory = dir;
        Session.SaveConfig();
        await Session.AuditAsync("KATALOG_ZALACZNIKOW", "Settings", null, dir);
    }, "Zapisano katalog załączników.");

    [RelayCommand]
    private void OpenDataFolder() => Dialogs.OpenInSystem(Session.Paths.Root);
}

public sealed record SchemaItem(string Name, string State, string Purpose, string InfoUrl, bool CanDownload, Infrastructure.Xml.KnownSchema Schema);
