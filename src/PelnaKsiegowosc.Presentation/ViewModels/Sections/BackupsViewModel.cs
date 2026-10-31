using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Szyfrowane kopie zapasowe: ręczne i automatyczne, weryfikacja, odtwarzanie do nowej lokalizacji, kopie w Object Storage, klucz kopii.</summary>
public sealed partial class BackupsViewModel : SectionViewModel
{
    public BackupsViewModel(AppSession s, IDialogService d) : base(s, d, "Backupy", "💾") { }

    [ObservableProperty] private TableData _local = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedLocal;
    [ObservableProperty] private TableData _remote = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedRemote;
    [ObservableProperty] private TableData _history = TableData.Empty;
    [ObservableProperty] private string _keyInfo = "";

    [ObservableProperty] private bool _automatic;
    [ObservableProperty] private string _intervalHours = "";
    [ObservableProperty] private bool _onExit;
    [ObservableProperty] private string _retain = "";
    [ObservableProperty] private bool _includeAttachments;
    [ObservableProperty] private bool _upload;
    [ObservableProperty] private string _directory = "";

    protected override async Task LoadCoreAsync()
    {
        var b = Session.Config.Backup;
        Automatic = b.AutomaticEnabled; IntervalHours = b.IntervalHours.ToString(); OnExit = b.BackupOnExit; Retain = b.RetainLocalCount.ToString();
        IncludeAttachments = b.IncludeAttachments; Upload = b.UploadToObjectStorage; Directory = Session.BackupDirectory;
        KeyInfo = Session.StoredBackupKey() is null
            ? "Brak klucza kopii zapasowych na tym stanowisku — wygeneruj nowy klucz albo wprowadź klucz odzyskiwania."
            : $"Klucz kopii zapisany w: {Session.Secrets.ProviderName}. Klucz odzyskiwania przechowuj poza komputerem — bez niego kopii nie da się odtworzyć.";
        var t = new TableData(new TableColumn("Plik", Width: 2.5), new TableColumn("Utworzono", Width: 1.1), new TableColumn("Rozmiar (MB)", ColumnKind.Decimal, 0.7));
        foreach (var f in BackupService.ListLocal(Session.BackupDirectory)) t.Add(f.FullName, f.Name, f.LastWriteTimeUtc.ToPlDateTime(), Math.Round(f.Length / 1048576m, 2));
        Local = t;
        await using var db = Session.OpenDb();
        var h = new TableData(new TableColumn("Czas", Width: 1.1), new TableColumn("Plik", Width: 2), new TableColumn("Cel", Width: 1.5), new TableColumn("Zweryfikowana", Width: 0.6),
            new TableColumn("Status", Width: 1), new TableColumn("Kopia zdalna", Width: 1.5), new TableColumn("Błąd", Width: 1.5));
        foreach (var r in await db.BackupRecords.AsNoTracking().OrderByDescending(r => r.CreatedAtUtc).Take(500).ToListAsync())
            h.Add(r.Id, r.CreatedAtUtc.ToPlDateTime(), r.FileName, r.Target, r.Verified, r.Status, r.RemoteKey, r.Error);
        History = h;
    }

    [RelayCommand]
    private Task SaveSettingsAsync() => ChangeAsync(() =>
    {
        var b = Session.Config.Backup;
        b.AutomaticEnabled = Automatic; b.IntervalHours = Math.Max(1, Parse.Int(IntervalHours, "Odstęp (godziny)")); b.BackupOnExit = OnExit;
        b.RetainLocalCount = Math.Max(0, Parse.Int(Retain, "Liczba kopii")); b.IncludeAttachments = IncludeAttachments; b.UploadToObjectStorage = Upload;
        b.LocalDirectory = string.IsNullOrWhiteSpace(Directory) || Path.GetFullPath(Directory) == Session.Paths.DefaultBackupDirectory ? null : Path.GetFullPath(Directory.Trim());
        if (b.LocalDirectory is not null) System.IO.Directory.CreateDirectory(b.LocalDirectory);
        Session.SaveConfig();
        return Task.CompletedTask;
    }, "Zapisano ustawienia kopii zapasowych.");

    [RelayCommand]
    private async Task PickDirectoryAsync()
    {
        if (await Dialogs.PickFolderAsync("Katalog kopii zapasowych") is { } dir) Directory = dir;
    }

    [RelayCommand]
    private Task BackupNowAsync() => ChangeAsync(async () =>
    {
        var r = await Session.BackupAsync("ręczna", upload: true);
        await Dialogs.InfoAsync("Kopia zapasowa", $"Utworzono i zweryfikowano:{Environment.NewLine}{r.FilePath}{Environment.NewLine}Rozmiar: {r.SizeBytes:N0} B" +
            (r.RemoteKey is not null ? $"{Environment.NewLine}Object Storage: {r.RemoteKey}" : "") + (r.RemoteError is not null ? $"{Environment.NewLine}Błąd wysyłki: {r.RemoteError}" : ""));
    }, "Kopia zapasowa wykonana.");

    private async Task<BackupKey?> KeyForArchiveAsync()
    {
        if (Session.StoredBackupKey() is { } k) return k;
        var text = await Dialogs.PromptAsync("Klucz kopii", "Na tym stanowisku nie zapisano klucza. Wpisz klucz odzyskiwania (grupy znaków rozdzielone myślnikami) albo hasło kopii:", password: true);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return TryParseRecoveryKey(text) is { } raw ? new BackupKey.RawKey(raw) : new BackupKey.Passphrase(text);
    }

    /// <summary>Klucz odzyskiwania (32 bajty w Base32) albo null, gdy tekst nim nie jest (np. hasło kopii).</summary>
    public static byte[]? TryParseRecoveryKey(string text)
    {
        try
        {
            var key = ArchiveCrypto.ParseRecoveryKey(text.Trim());
            return key.Length == 32 ? key : null;
        }
        catch (FormatException) { return null; }
    }

    private string SelectedFile => SelectedLocal?.Tag as string ?? throw new DomainException("Zaznacz plik kopii na liście.");

    [RelayCommand]
    private Task VerifyAsync() => RunAsync(async () =>
    {
        var file = SelectedFile;
        if (await KeyForArchiveAsync() is not { } key) return;
        var r = await new BackupService(Session.Clock, Session.Paths.TempDirectory).VerifyAsync(file, key);
        await Dialogs.ShowTextAsync("Weryfikacja kopii", r.Ok
            ? $"Kopia jest kompletna i nienaruszona.{Environment.NewLine}Utworzona: {r.Manifest!.CreatedUtc.ToPlDateTime()}, wersja programu {r.Manifest.AppVersion}, jednostka: {r.Manifest.CompanyName} ({r.Manifest.CompanyNip}), plików: {r.Manifest.Files.Count}."
            : "Kopia NIE przeszła weryfikacji:" + Environment.NewLine + string.Join(Environment.NewLine, r.Problems));
    });

    [RelayCommand]
    private Task RestoreAsync() => RunAsync(async () =>
    {
        var file = SelectedFile;
        if (await KeyForArchiveAsync() is not { } key) return;
        var dir = await Dialogs.PickFolderAsync("Katalog docelowy odtworzenia (pusty lub nowy)");
        if (dir is null) return;
        var target = Path.Combine(dir, "odtworzona-" + Session.Clock.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var r = await new BackupService(Session.Clock, Session.Paths.TempDirectory).RestoreToSqliteAsync(file, key, target,
            Session.Connector.SqliteKeyForNewFile(RestoredProfile(target)), Session.AttachmentsKeyFor(Path.Combine(target, "Zalaczniki"), RestoredProfile(target)));
        if (!r.Ok) { await Dialogs.ShowTextAsync("Odtwarzanie", "Odtwarzanie zakończone z błędami:" + Environment.NewLine + string.Join(Environment.NewLine, r.Problems)); return; }
        await Session.AuditAsync("KOPIA_ODTWORZONA", "Backup", r.Manifest.ArchiveId.ToString(), $"{Path.GetFileName(file)} → {target}");
        if (await Dialogs.ConfirmAsync("Odtwarzanie", $"Kopię odtworzono do:{Environment.NewLine}{r.DatabasePath}{Environment.NewLine}{Environment.NewLine}" +
                "Przełączyć program na odtworzoną bazę? Bieżąca baza pozostanie bez zmian (można do niej wrócić w Ustawieniach). Wymagane ponowne uruchomienie programu."))
        {
            Session.Config.OtherProfiles.Add(Session.Config.ActiveDatabase);
            Session.Config.ActiveDatabase = new DatabaseProfile { Name = "Baza odtworzona z kopii", Engine = DatabaseEngine.Sqlite, SqlitePath = r.DatabasePath };
            Session.Config.AttachmentsDirectory = r.AttachmentsPath;
            Session.SaveConfig();
            await Dialogs.InfoAsync("Odtwarzanie", "Zamknij i uruchom ponownie program, aby pracować na odtworzonej bazie.");
        }
    });

    [RelayCommand]
    private Task GenerateKeyAsync() => ChangeAsync(async () =>
    {
        if (Session.StoredBackupKey() is not null &&
            !await Dialogs.ConfirmAsync("Nowy klucz kopii", "Nowy klucz zastąpi obecny na tym stanowisku. Dotychczasowe kopie nadal wymagają STAREGO klucza odzyskiwania. Kontynuować?")) return;
        var recovery = Session.GenerateBackupKey();
        await Session.AuditAsync("KLUCZ_KOPII_WYGENEROWANY", "Backup", null, Session.Secrets.ProviderName);
        await Dialogs.ShowTextAsync("Klucz odzyskiwania — ZAPISZ GO", $"Klucz odzyskiwania kopii zapasowych:{Environment.NewLine}{Environment.NewLine}{recovery}{Environment.NewLine}{Environment.NewLine}" +
            "Zapisz go na papierze lub w menedżerze haseł i przechowuj poza komputerem. Bez tego klucza nie da się odtworzyć kopii na innym komputerze ani po awarii systemu. Program nie pokaże go ponownie.");
    }, "Wygenerowano klucz kopii.");

    [RelayCommand]
    private Task EnterRecoveryKeyAsync() => ChangeAsync(async () =>
    {
        var text = await Dialogs.PromptAsync("Klucz odzyskiwania", "Wpisz klucz odzyskiwania kopii zapasowych:", password: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        var key = TryParseRecoveryKey(text) ?? throw new DomainException("Nieprawidłowy klucz odzyskiwania.");
        Session.StoreBackupKey(key);
        await Session.AuditAsync("KLUCZ_KOPII_WPROWADZONY", "Backup", null, Session.Secrets.ProviderName);
    }, "Zapisano klucz kopii na tym stanowisku.");

    [RelayCommand]
    private Task ListRemoteAsync() => RunAsync(async () =>
    {
        using var target = Session.RemoteTarget() ?? throw new DomainException("Object Storage nie jest skonfigurowany (sekcja „Integracje”).");
        var t = new TableData(new TableColumn("Obiekt", Width: 3), new TableColumn("Rozmiar (MB)", ColumnKind.Decimal, 0.7), new TableColumn("Zmieniono", Width: 1.1));
        foreach (var (key, size, at) in await target.ListAsync()) t.Add(key, key, Math.Round(size / 1048576m, 2), at.ToPlDateTime());
        Remote = t;
    });

    [RelayCommand]
    private Task DownloadRemoteAsync() => ChangeAsync(async () =>
    {
        if (SelectedRemote?.Tag is not string key) throw new DomainException("Zaznacz obiekt na liście kopii zdalnych.");
        using var target = Session.RemoteTarget() ?? throw new DomainException("Object Storage nie jest skonfigurowany.");
        var local = Path.Combine(Session.BackupDirectory, Path.GetFileName(key));
        if (File.Exists(local)) throw new DomainException($"Plik {local} już istnieje.");
        await target.DownloadAsync(key, local);
    }, "Pobrano kopię do katalogu kopii lokalnych (suma SHA-256 zgodna).");

    private static Data.DatabaseProfile RestoredProfile(string target) => new() { Engine = Data.DatabaseEngine.Sqlite, SqlitePath = Path.Combine(target, "ksiegi.db") };
}
