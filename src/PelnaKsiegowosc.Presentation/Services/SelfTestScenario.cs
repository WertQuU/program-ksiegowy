using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Presentation.ViewModels;
using PelnaKsiegowosc.Presentation.ViewModels.Sections;
using PelnaKsiegowosc.Presentation.ViewModels.Wizard;

namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>
/// Scenariusz kontroli uruchomienia: kreator w katalogu tymczasowym (nowa baza, jednostka, rok, plan kont, polityka, klucz kopii),
/// następnie wczytanie wszystkich sekcji, wystawienie i zaksięgowanie faktury, raport i kopia zapasowa.
/// Używany przez program (<c>--test-uruchomienia</c>) i testy automatyczne.
/// </summary>
public static class SelfTestScenario
{
    public const string TestNip = "5260250274";

    public static (AppSession session, AutomaticDialogService dialogs) CreateSession(string root, ISecretStore? secrets = null)
    {
        var paths = new AppPaths(root);
        // Ten scenariusz powstaje wyłącznie na izolowanym katalogu testowym, bez serwera ksiąg.
        var config = AppConfig.LoadOrCreate(paths);
        config.ActiveDatabase = new PelnaKsiegowosc.Data.DatabaseProfile
        {
            Engine = PelnaKsiegowosc.Data.DatabaseEngine.Sqlite,
            SqlitePath = paths.DefaultDatabaseFile
        };
        var session = new AppSession(paths, config, secrets ?? new InMemorySecretStore(), new SystemClock(), isolatedSelfTest: true);
        return (session, new AutomaticDialogService());
    }

    public static async Task<SetupWizardViewModel> RunWizardAsync(AppSession session, AutomaticDialogService dialogs, LegalForm form = LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia)
    {
        var w = new SetupWizardViewModel(session, dialogs) { TermsAccepted = true };
        // Scenariusz sprawdza samodzielny tryb SQLite.
        w.Database.UsePostgres = false;
        await w.NextCommand.ExecuteAsync(null);
        await w.NextCommand.ExecuteAsync(null); // baza (SQLite domyślnie w katalogu danych)
        w.Company.LegalForm = w.Company.LegalFormChoices.First(c => c.Value == form);
        w.Company.FullName = "Przykładowa Jednostka " + LegalForms.Designations(form).FirstOrDefault();
        w.Company.Nip = TestNip;
        w.Company.Krs = "0000123456";
        w.Company.Street = "ul. Testowa";
        w.Company.BuildingNumber = "1";
        w.Company.PostalCode = "00-001";
        w.Company.City = "Warszawa";
        w.Company.TaxOfficeCode = "1471";
        await w.NextCommand.ExecuteAsync(null);
        var y = session.Clock.Today.Year;
        w.YearStart = new DateOnly(y, 1, 1).ToPl();
        w.YearEnd = new DateOnly(y, 12, 31).ToPl();
        await w.NextCommand.ExecuteAsync(null);
        await w.NextCommand.ExecuteAsync(null); // plan kont
        w.PolicyContent = w.PolicyContent.Replace("[DO UZUPEŁNIENIA", "[uzupełniono");
        w.ApprovePolicy = true;
        w.PolicyApprovedBy = "Jan Kowalski — prezes zarządu";
        await w.NextCommand.ExecuteAsync(null);
        w.ManagerFirstName = "Jan";
        w.ManagerLastName = "Kowalski";
        await w.NextCommand.ExecuteAsync(null);
        await w.GenerateKeyCommand.ExecuteAsync(null);
        w.RecoveryKeySaved = true;
        await w.NextCommand.ExecuteAsync(null);
        await w.FinishCommand.ExecuteAsync(null);
        if (!w.Finished) throw new InvalidOperationException("Kreator nie zakończył się: " + string.Join(" | ", dialogs.Errors) + $" (krok {w.Step})");
        // dane scenariusza: kurs EUR do limitu 9% CIT (w pracy pobierany z NBP przyciskiem w sekcji CIT) — autotest działa bez internetu
        await using (var db = session.OpenDb())
        {
            foreach (var c in await db.Companies.AsNoTracking().ToListAsync())
            {
                if (!LegalForms.IsCitTaxpayer(c.LegalForm)) continue;
                var cit = new Application.Cit.CitService(db, OperatorIdentity.For(c.Id), session.Clock);
                foreach (var fy in await db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == c.Id).ToListAsync())
                    await cit.SetLimitEurRateAsync(fy.StartDate, 4.2156m);
            }
        }
        return w;
    }

    /// <summary>Operacje na ekranach: kontrahent, faktura sprzedaży (zatwierdzenie, księgowanie), raport, kopia.</summary>
    public static async Task RunOperationsAsync(MainViewModel main, AutomaticDialogService dialogs)
    {
        var cps = main.Sections.OfType<CounterpartiesViewModel>().Single();
        await cps.LoadAsync();
        await cps.NewCommand.ExecuteAsync(null);
        cps.Name = "Klient Testowy sp. z o.o.";
        cps.Nip = "7740001454";
        cps.Street = "Rynek"; cps.Building = "1"; cps.PostalCode = "30-001"; cps.City = "Kraków";
        await cps.SaveCommand.ExecuteAsync(null);

        var sales = main.Sections.OfType<SalesViewModel>().Single();
        await sales.LoadAsync();
        await sales.NewCommand.ExecuteAsync(null);
        sales.Editor.Counterparty = sales.Editor.Counterparties.First();
        sales.Editor.Lines[0].Name = "Usługa księgowa";
        sales.Editor.Lines[0].UnitNetPrice = "1 000,00";
        await sales.SaveCommand.ExecuteAsync(null);
        sales.Selected = sales.Documents.Rows.FirstOrDefault() ?? throw new InvalidOperationException("Nie utworzono faktury: " + string.Join(" | ", dialogs.Errors));
        await sales.ApproveCommand.ExecuteAsync(null);
        sales.Selected = sales.Documents.Rows.First();
        await sales.PostCommand.ExecuteAsync(null);

        var reports = main.Sections.OfType<ReportsViewModel>().Single();
        await reports.LoadAsync();
        reports.Report = reports.Reports.First(r => r.Value == ReportKind.ZestawienieObrotowISald);
        await reports.GenerateCommand.ExecuteAsync(null);
        if (reports.Preview.Rows.Count == 0) throw new InvalidOperationException("Zestawienie obrotów i sald jest puste po zaksięgowaniu faktury.");

        var backups = main.Sections.OfType<BackupsViewModel>().Single();
        await backups.LoadAsync();
        await backups.BackupNowCommand.ExecuteAsync(null);
        await backups.LoadAsync();
        if (backups.Local.Rows.Count == 0) throw new InvalidOperationException("Brak kopii zapasowej po wykonaniu kopii.");
    }
}
