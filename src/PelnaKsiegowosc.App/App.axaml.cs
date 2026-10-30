using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PelnaKsiegowosc.App.Services;
using PelnaKsiegowosc.App.Views;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Infrastructure.Secrets;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels;
using PelnaKsiegowosc.Presentation.ViewModels.Sections;
using PelnaKsiegowosc.Presentation.ViewModels.Wizard;

namespace PelnaKsiegowosc.App;

public partial class App : Avalonia.Application
{
    private DispatcherTimer? _backupTimer;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var splash = new Window { Title = "Pełna Księgowość", Width = 420, Height = 140, WindowStartupLocation = WindowStartupLocation.CenterScreen, CanResize = false,
                Content = new TextBlock { Text = "Uruchamianie programu…", Margin = new Thickness(24), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } };
            desktop.MainWindow = splash;
            splash.Show();
            Dispatcher.UIThread.Post(async () => await StartAsync(desktop, splash));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, Window splash)
    {
        Window? current = splash;
        var dialogs = new DialogService(() => current);
        try
        {
            var paths = new AppPaths();
            ErrorLog.Directory = paths.LogsDirectory;
            var config = AppConfig.LoadOrCreate(paths);
            var session = new AppSession(paths, config, SecretStoreFactory.Create(paths), new SystemClock());

            if (!config.SetupCompleted || config.TermsAcceptedVersion != Terms.Version || !await DatabaseHasBooksAsync(session))
            {
                var wizardVm = new SetupWizardViewModel(session, dialogs);
                var wizard = new WizardWindow { DataContext = wizardVm };
                var done = new TaskCompletionSource<bool>();
                wizardVm.Completed += (_, _) => { done.TrySetResult(true); };
                wizard.Closed += (_, _) => done.TrySetResult(wizardVm.Finished);
                current = wizard;
                desktop.MainWindow = wizard;
                wizard.Show();
                splash.Close();
                splash = wizard;
                if (!await done.Task) { desktop.Shutdown(0); return; }
            }

            while (true)
            {
                var opened = await session.OpenAsync();
                if (opened.Success)
                {
                    if (opened.PreMigrationBackup is { } b)
                        await dialogs.InfoAsync("Aktualizacja bazy", $"Schemat bazy został zaktualizowany. Przed aktualizacją wykonano kopię: {b}");
                    break;
                }
                var choice = await StartupErrorWindow.ShowAsync(current!, opened.Error ?? "Nie udało się otworzyć bazy.", session.Profile.Engine == Data.DatabaseEngine.PostgreSql);
                if (choice == StartupChoice.Exit) { desktop.Shutdown(1); return; }
                if (choice == StartupChoice.Emergency && !await StartEmergencyAsync(session, dialogs)) continue;
            }

            var mainVm = new MainViewModel(session, dialogs);
            var main = new MainWindow { DataContext = mainVm };
            current = main;
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
            splash.Close();
            await mainVm.StartAsync();

            _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _backupTimer.Tick += async (_, _) => await mainVm.AutomaticBackupAsync();
            _backupTimer.Start();
            await mainVm.AutomaticBackupAsync();
            _ = Task.Run(() => RegulationsCheck.RunDailyAsync(session));
            main.ClosingAccepted += async () => await mainVm.BackupOnExitAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            await dialogs.ErrorAsync("Błąd uruchomienia", ViewModelBase.Describe(ex));
            desktop.Shutdown(2);
        }
    }

    private static async Task<bool> DatabaseHasBooksAsync(AppSession session)
    {
        var test = await session.Connector.TestAsync(session.Profile);
        return !test.Success || !test.DatabaseEmpty;
    }

    /// <summary>Tryb awaryjny: osobna kopia robocza SQLite odtworzona z ostatniej lokalnej kopii zapasowej.</summary>
    private static async Task<bool> StartEmergencyAsync(AppSession session, IDialogService dialogs)
    {
        var key = session.StoredBackupKey();
        var latest = BackupService.ListLocal(session.BackupDirectory).FirstOrDefault();
        if (key is null || latest is null)
        {
            await dialogs.ErrorAsync("Tryb awaryjny", "Brak lokalnej kopii zapasowej lub klucza kopii na tym stanowisku — nie można utworzyć kopii roboczej.");
            return false;
        }
        if (!await dialogs.ConfirmAsync("Tryb awaryjny", $"Utworzyć OSOBNĄ kopię roboczą z kopii {latest.Name} ({latest.LastWriteTimeUtc.ToPlDateTime()})?{Environment.NewLine}" +
                "Zmiany w kopii roboczej nie trafią automatycznie na serwer — wymagają późniejszego uzgodnienia (ponownego wprowadzenia dokumentów).")) return false;
        var profile = DatabaseConnector.EmergencyProfile(session.Paths, session.Clock.UtcNow);
        var r = await new BackupService(session.Clock, session.Paths.TempDirectory).RestoreToSqliteAsync(latest.FullName, key, Path.GetDirectoryName(profile.SqlitePath!)!);
        if (!r.Ok)
        {
            await dialogs.ShowTextAsync("Tryb awaryjny", "Odtworzenie kopii nie powiodło się:" + Environment.NewLine + string.Join(Environment.NewLine, r.Problems));
            return false;
        }
        profile.SqlitePath = r.DatabasePath;
        session.Config.OtherProfiles.Add(session.Config.ActiveDatabase);
        session.Config.ActiveDatabase = profile;
        session.Config.AttachmentsDirectory = r.AttachmentsPath;
        session.SaveConfig();
        return true;
    }
}
