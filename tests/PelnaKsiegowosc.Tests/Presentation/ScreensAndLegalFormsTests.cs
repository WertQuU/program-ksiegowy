using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Infrastructure.Secrets;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels;
using PelnaKsiegowosc.Presentation.ViewModels.Sections;
using PelnaKsiegowosc.Presentation.ViewModels.Wizard;

namespace PelnaKsiegowosc.Tests.Presentation;

[CollectionDefinition("Konsola i zmienne środowiskowe", DisableParallelization = true)]
public sealed class ConsoleCollection;

internal sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pk-ui-tests", Guid.NewGuid().ToString("N"));
    public TempRoot() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class LegalFormRulesTests
{
    [Theory]
    [InlineData(LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia, "Kapitał zakładowy", true)]
    [InlineData(LegalForm.ProstaSpolkaAkcyjna, "Kapitał akcyjny", true)]
    [InlineData(LegalForm.SpolkaJawna, "Kapitał wspólników", false)]
    [InlineData(LegalForm.Fundacja, "Fundusz założycielski", true)]
    [InlineData(LegalForm.DzialalnoscGospodarczaOsobyFizycznej, "Kapitał właściciela", false)]
    public async Task Wizard_creates_books_adapted_to_legal_form(LegalForm form, string capitalName, bool cit)
    {
        using var root = new TempRoot();
        var (session, dialogs) = SelfTestScenario.CreateSession(root.Path);
        await SelfTestScenario.RunWizardAsync(session, dialogs, form);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(form, session.Company!.LegalForm);
        await using var db = session.OpenDb();
        var capital = await db.Accounts.FirstAsync(a => a.Number == "801");
        Assert.Equal(capitalName, capital.Name);
        Assert.Equal(cit, LegalForms.IsCitTaxpayer(form));
        var policy = await db.AccountingPolicies.FirstAsync();
        Assert.Contains(LegalForms.Name(form), policy.Content);
        Assert.True(policy.ApprovedByManagement);
        Assert.Equal(2, await db.MoneyAccounts.CountAsync());

        var main = new MainViewModel(session, dialogs);
        var citVm = main.Sections.OfType<CitViewModel>().Single();
        await citVm.LoadAsync();
        Assert.Equal(cit, citVm.IsApplicable);
        await citVm.ComputeAdvanceCommand.ExecuteAsync(null);
        if (cit) Assert.Empty(dialogs.Errors);
        else Assert.Contains(dialogs.Errors, e => e.Contains("nie jest podatnikiem CIT"));
    }

    [Fact]
    public void Company_validation_depends_on_legal_form()
    {
        Company C(LegalForm f, string name, string? krs) => new()
        {
            LegalForm = f, FullName = name, Nip = "5260250274", Krs = krs, Street = "ul. A", BuildingNumber = "1", PostalCode = "00-001", City = "Warszawa", TaxOfficeCode = "1471"
        };
        Assert.Contains(C(LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia, "Alfa", "0000123456").Validate().Warnings, w => w.Contains("sp. z o.o."));
        Assert.Empty(C(LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia, "Alfa sp. z o.o.", "0000123456").Validate().Warnings);
        Assert.Empty(C(LegalForm.DzialalnoscGospodarczaOsobyFizycznej, "Jan Kowalski Usługi", null).Validate().Warnings);
        Assert.Contains(C(LegalForm.Fundacja, "Fundacja Beta", null).Validate().Warnings, w => w.Contains("KRS"));
        Assert.Empty(C(LegalForm.SpolkaCywilna, "Gamma s.c.", null).Validate().Warnings);
    }

    [Fact]
    public void Capital_event_requirements_depend_on_legal_form()
    {
        var increase = new CapitalEvent { Type = CapitalEventType.PodwyzszenieKapitaluAkcyjnego, ShareCapitalAmount = 1000, ResolutionReference = "Uchwała 1/2026" };
        Assert.Contains(increase.MissingDocuments(LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia), m => m.Contains("KRS"));
        Assert.DoesNotContain(increase.MissingDocuments(LegalForm.SpolkaCywilna), m => m.Contains("KRS"));
        var payout = new CapitalEvent { Type = CapitalEventType.WyplataDywidendy, TotalAmount = 500 };
        Assert.Contains(payout.MissingDocuments(LegalForm.ProstaSpolkaAkcyjna), m => m.Contains("300^15"));
        Assert.Contains(payout.MissingDocuments(LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia), m => m.Contains("191"));
        var take = new CapitalEvent { Type = CapitalEventType.ObjecieAkcjiWkladPieniezny, ShareCapitalAmount = 5000, AgreementReference = "Umowa", ShareholderId = Guid.NewGuid() };
        Assert.Contains(take.MissingDocuments(LegalForm.SpolkaAkcyjna), m => m.Contains("akcje"));
        Assert.Empty(take.MissingDocuments(LegalForm.SpolkaJawna));
    }
}

public class ScreensTests
{
    [Fact]
    public async Task All_sections_load_and_invoice_can_be_issued_and_posted_from_screens()
    {
        using var root = new TempRoot();
        var (session, dialogs) = SelfTestScenario.CreateSession(root.Path);
        await SelfTestScenario.RunWizardAsync(session, dialogs);
        var main = new MainViewModel(session, dialogs);
        Assert.Equal(Enum.GetValues<PelnaKsiegowosc.Application.Assistant.AppArea>().Length, main.Sections.Count);
        foreach (var s in main.Sections)
        {
            await s.LoadAsync();
            Assert.True(s.Loaded, $"{s.Title}: {string.Join(" | ", dialogs.Errors)}");
        }
        Assert.Empty(dialogs.Errors);
        await SelfTestScenario.RunOperationsAsync(main, dialogs);
        Assert.Empty(dialogs.Errors);

        var ledger = main.Sections.OfType<LedgerViewModel>().Single();
        await ledger.LoadAsync();
        Assert.Contains(ledger.Entries.Rows, r => r.Cells[2] == "SPR");
        var dashboard = main.Sections.OfType<DashboardViewModel>().Single();
        await dashboard.LoadAsync();
        // jedyny możliwy błąd: faktura dla firmy bez numeru KSeF (wysyłka nie jest częścią scenariusza)
        Assert.All(dashboard.Issues.Where(i => i.Issue.Severity == PelnaKsiegowosc.Application.Assistant.IssueSeverity.Blad), i => Assert.Equal("KSEF_BRAK", i.Issue.Code));
        Assert.NotEmpty(dashboard.Deadlines);
    }

    [Fact]
    public async Task Manual_entry_storno_and_validation_messages_from_ledger_screen()
    {
        using var root = new TempRoot();
        var (session, dialogs) = SelfTestScenario.CreateSession(root.Path);
        await SelfTestScenario.RunWizardAsync(session, dialogs);
        var ledger = new MainViewModel(session, dialogs).Sections.OfType<LedgerViewModel>().Single();
        await ledger.LoadAsync();
        await ledger.NewEntryCommand.ExecuteAsync(null);
        ledger.Description = "Wpłata gotówki do banku";
        ledger.DocumentNumber = "PK/1";
        ledger.Lines.Lines[0].Account = "130-01"; ledger.Lines.Lines[0].Debit = "500,00";
        ledger.Lines.Lines[1].Account = "100"; ledger.Lines.Lines[1].Credit = "400,00";
        await ledger.SaveAndPostCommand.ExecuteAsync(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("różni się"));
        dialogs.Errors.Clear();
        ledger.Lines.Lines[1].Credit = "500,00";
        await ledger.SaveAndPostCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        ledger.Selected = ledger.Entries.Rows.Single();
        dialogs.Prompts.Enqueue("Błędna kwota");
        await ledger.ReverseCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(2, ledger.Entries.Rows.Count);
        Assert.Contains(ledger.Entries.Rows, r => r.Cells[9] == "wystornowany");
    }

    [Fact]
    public async Task Second_workstation_connects_to_existing_books_without_creating_new_unit()
    {
        using var root = new TempRoot();
        var (first, d1) = SelfTestScenario.CreateSession(Path.Combine(root.Path, "a"));
        await SelfTestScenario.RunWizardAsync(first, d1);
        var (second, d2) = SelfTestScenario.CreateSession(Path.Combine(root.Path, "b"));
        var w = new SetupWizardViewModel(second, d2) { TermsAccepted = true };
        await w.NextCommand.ExecuteAsync(null);
        w.Database.SqlitePath = first.Profile.SqlitePath!;
        d2.Prompts.Enqueue(first.Connector.ExportSqliteKey(first.Profile)!); // baza zaszyfrowana — klucz przekazany z pierwszego stanowiska
        await w.NextCommand.ExecuteAsync(null);
        Assert.True(w.ConnectToExisting);
        Assert.Equal(WizardStep.KopieZapasowe, w.Step);
        w.SkipBackupKey = true;
        await w.NextCommand.ExecuteAsync(null);
        await w.FinishCommand.ExecuteAsync(null);
        Assert.True(w.Finished, string.Join(" | ", d2.Errors));
        Assert.Equal(first.Company!.Id, second.Company!.Id);
        await using var db = second.OpenDb();
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Wizard_requires_terms_acceptance_and_valid_company_data()
    {
        using var root = new TempRoot();
        var (session, dialogs) = SelfTestScenario.CreateSession(root.Path);
        var w = new SetupWizardViewModel(session, dialogs);
        await w.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Warunki, w.Step);
        Assert.Contains(dialogs.Errors, e => e.Contains("zaakceptuj"));
        Assert.Contains("LICENCJA", w.TermsText);
        w.TermsAccepted = true;
        await w.NextCommand.ExecuteAsync(null);
        await w.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Jednostka, w.Step);
        w.Company.FullName = "Firma sp. z o.o.";
        w.Company.Nip = "1234567890";
        await w.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Jednostka, w.Step);
        Assert.Contains(dialogs.Errors, e => e.Contains("NIP"));
    }
}

[Collection("Konsola i zmienne środowiskowe")]
public class CommandLineTests
{
    private static async Task<(int code, string output)> RunAsync(params string[] args)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(sw);
        Console.SetError(sw);
        try { return (await PelnaKsiegowosc.Cli.Program.Main(args), sw.ToString()); }
        finally
        {
            Console.SetOut(original);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task Cli_backup_verify_restore_and_integrity()
    {
        using var root = new TempRoot();
        Environment.SetEnvironmentVariable("PELNAKSIEGOWOSC_HOME", root.Path);
        Environment.SetEnvironmentVariable("PELNAKSIEGOWOSC_SECRETS", "plik");
        try
        {
            var (session, dialogs) = SelfTestScenario.CreateSession(root.Path, new FileSecretStore(Path.Combine(root.Path, "Sekrety")));
            await SelfTestScenario.RunWizardAsync(session, dialogs);

            var (code, output) = await RunAsync("info");
            Assert.Equal(0, code);
            Assert.Contains("NIP 5260250274", output);

            (code, output) = await RunAsync("kopia", "--powod", "test CLI");
            Assert.True(code == 0, output);
            var file = output.Split('\n').First(l => l.StartsWith("Kopia: "))["Kopia: ".Length..].Trim();

            (code, output) = await RunAsync("weryfikuj", file);
            Assert.True(code == 0, output);
            Assert.Contains("Kopia poprawna", output);

            var target = Path.Combine(root.Path, "odtworzenie");
            (code, output) = await RunAsync("odtworz", file, target);
            Assert.True(code == 0, output);
            Assert.True(File.Exists(Path.Combine(target, "ksiegi.db")) || Directory.EnumerateFiles(target, "*.db", SearchOption.AllDirectories).Any());

            (code, output) = await RunAsync("integralnosc");
            Assert.True(code == 0, output);

            (code, output) = await RunAsync("nieznane");
            Assert.Equal(1, code);
            (code, _) = await RunAsync("pomoc");
            Assert.Equal(0, code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PELNAKSIEGOWOSC_HOME", null);
            Environment.SetEnvironmentVariable("PELNAKSIEGOWOSC_SECRETS", null);
        }
    }
}

public class DesktopAppStartupTests
{
    [Fact]
    public void Desktop_application_self_test_builds_every_screen_headless()
    {
        var config = typeof(DesktopAppStartupTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PelnaKsiegowosc.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var app = Path.Combine(dir!.FullName, "src", "PelnaKsiegowosc.App", "bin", config, "net10.0", "PelnaKsiegowosc.dll");
        Assert.True(File.Exists(app), "Brak skompilowanego programu: " + app);
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(app);
        psi.ArgumentList.Add("--test-uruchomienia");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(240_000), "Przekroczono czas kontroli uruchomienia.");
        var output = stdout.Result + stderr.Result;
        Assert.True(p.ExitCode == 0, output);
        Assert.Contains("KONTROLA URUCHOMIENIA: OK", output);
    }
}
