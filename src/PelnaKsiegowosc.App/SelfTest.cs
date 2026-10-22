using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PelnaKsiegowosc.App.Views;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels;

namespace PelnaKsiegowosc.App;

/// <summary>Kontrola uruchomienia bez okna (platforma headless): kreator, wszystkie ekrany, podstawowe operacje.</summary>
public static class SelfTest
{
    public static int Run(TextWriter log)
    {
        var root = Path.Combine(Path.GetTempPath(), "pelnaksiegowosc-test-" + Guid.NewGuid().ToString("N"));
        var cts = new CancellationTokenSource();
        var result = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try { result = await RunAsync(root, log); }
            catch (Exception ex) { log.WriteLine("BŁĄD: " + ex); result = 1; }
            finally { cts.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(cts.Token);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        log.WriteLine(result == 0 ? "KONTROLA URUCHOMIENIA: OK" : "KONTROLA URUCHOMIENIA: BŁĄD");
        return result;
    }

    private static async Task<int> RunAsync(string root, TextWriter log)
    {
        log.WriteLine("Start kontroli…"); log.Flush();
        var (session, dialogs) = SelfTestScenario.CreateSession(root);
        var wizardVm = new SetupWizardViewModelFactory(session, dialogs).Create();
        var wizard = new WizardWindow { DataContext = wizardVm, Width = 980, Height = 760 };
        wizard.Show();
        Pump(wizard);
        Shot(wizard, "00-kreator-warunki");
        wizardVm.TermsAccepted = true;
        await wizardVm.NextCommand.ExecuteAsync(null);
        Pump(wizard);
        Shot(wizard, "00-kreator-baza");
        await wizardVm.NextCommand.ExecuteAsync(null);
        Pump(wizard);
        Shot(wizard, "00-kreator-jednostka");
        wizard.Close();
        log.WriteLine("Okno kreatora: OK");

        await SelfTestScenario.RunWizardAsync(session, dialogs);
        log.WriteLine($"Kreator: utworzono księgi ({session.Company?.FullName}, rok {session.Year?.Name})");
        var open = await session.OpenAsync();
        if (!open.Success) throw new InvalidOperationException(open.Error);

        var main = new MainViewModel(session, dialogs);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 860 };
        window.Show();
        await main.StartAsync();
        var failed = 0;
        foreach (var section in main.Sections)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            main.SelectedSection = section;
            await section.LoadAsync();
            var loadMs = sw.ElapsedMilliseconds;
            Pump(window);
            if (Environment.GetEnvironmentVariable("PK_SELFTEST_VERBOSE") == "1") log.WriteLine($"    wczytanie {loadMs} ms, układ {sw.ElapsedMilliseconds - loadMs} ms");
            var viewName = section.GetType().Name.Replace("ViewModel", "View");
            var hasView = window.GetVisualDescendants().Any(v => v.GetType().Name == viewName);
            var ok = hasView && dialogs.Errors.Count == 0;
            if (!ok) failed++;
            Shot(window, $"{main.Sections.ToList().IndexOf(section) + 1:00}-{viewName}");
            log.WriteLine($"  {(ok ? "OK " : "BŁĄD")} {section.Title}{(hasView ? "" : " — brak widoku")}{(dialogs.Errors.Count > 0 ? " — " + string.Join(" | ", dialogs.Errors) : "")}");
            dialogs.Errors.Clear();
        }
        await SelfTestScenario.RunOperationsAsync(main, dialogs);
        var sales = main.Sections.OfType<Presentation.ViewModels.Sections.SalesViewModel>().Single();
        main.SelectedSection = sales;
        await sales.NewCommand.ExecuteAsync(null);
        Pump(window);
        Shot(window, "21-SalesEditor");
        sales.EditorVisible = false;
        var ledger = main.Sections.OfType<Presentation.ViewModels.Sections.LedgerViewModel>().Single();
        main.SelectedSection = ledger;
        await ledger.LoadAsync();
        await ledger.NewEntryCommand.ExecuteAsync(null);
        Pump(window);
        Shot(window, "22-LedgerEditor");
        if (dialogs.Errors.Count > 0) { log.WriteLine("Operacje: " + string.Join(" | ", dialogs.Errors)); failed++; }
        else log.WriteLine("Operacje (kontrahent, faktura, księgowanie, raport, kopia): OK");
        window.Close();
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Zrzut ekranu (tylko gdy ustawiono PK_SELFTEST_SCREENSHOTS — renderowanie Skia bez monitora).</summary>
    private static void Shot(Window w, string name)
    {
        if (Environment.GetEnvironmentVariable("PK_SELFTEST_SCREENSHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
    }

    private static void Pump(Window w)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }
    }

    private sealed class SetupWizardViewModelFactory(AppSession session, IDialogService dialogs)
    {
        public Presentation.ViewModels.Wizard.SetupWizardViewModel Create() => new(session, dialogs);
    }
}
