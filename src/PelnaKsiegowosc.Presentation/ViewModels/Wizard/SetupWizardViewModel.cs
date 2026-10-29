using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Wizard;

public enum WizardStep
{
    Warunki, Baza, Jednostka, RokObrotowy, PlanKont, Polityka, OsobaOdpowiedzialna, KopieZapasowe, Podsumowanie
}

/// <summary>
/// Kreator pierwszego uruchomienia. Kroki: akceptacja warunków licencji, wybór bazy (lokalna/serwer), dane jednostki,
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
    [NotifyPropertyChangedFor(nameof(IsTerms), nameof(IsDatabase), nameof(IsCompany), nameof(IsYear), nameof(IsChart), nameof(IsPolicy), nameof(IsPerson), nameof(IsBackup), nameof(IsSummary))]
    private WizardStep _step = WizardStep.Warunki;

    public bool IsTerms => Step == WizardStep.Warunki;
    public bool IsDatabase => Step == WizardStep.Baza;
    public bool IsCompany => Step == WizardStep.Jednostka;
    public bool IsYear => Step == WizardStep.RokObrotowy;
    public bool IsChart => Step == WizardStep.PlanKont;
    public bool IsPolicy => Step == WizardStep.Polityka;
    public bool IsPerson => Step == WizardStep.OsobaOdpowiedzialna;
    public bool IsBackup => Step == WizardStep.KopieZapasowe;
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
        _ => "Utworzenie ksiąg"
    };
    public bool CanGoBack => Step > WizardStep.Warunki && !Finished;
    public bool IsLastStep => Step == WizardStep.Podsumowanie;

    [ObservableProperty] private bool _termsAccepted;
    [ObservableProperty] private string _databaseInfo = "";
    [ObservableProperty] private bool _connectToExisting;

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

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private TableData _readiness = TableData.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoBack))] private bool _finished;

    [RelayCommand]
    private void Back()
    {
        if (Step == WizardStep.KopieZapasowe && ConnectToExisting) { Step = WizardStep.Baza; return; }
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
                await CheckDatabaseAsync();
                if (ConnectToExisting) { Step = WizardStep.KopieZapasowe; return; }
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
                ChartInfo = $"Zostanie utworzony plan kont „{ChartTemplate.Name}” ({ChartTemplate.Accounts.Count} kont) — koszty w układzie rodzajowym, RZiS w wariancie porównawczym, " +
                            $"konto 801: „{LegalForms.ShareCapitalName(form)}”. Plan kont jest elementem polityki rachunkowości: możesz go zmienić w sekcji „Plan kont”.";
                break;
            case WizardStep.PlanKont:
                if (string.IsNullOrEmpty(PolicyContent))
                {
                    var start = Parse.Date(YearStart, "Początek roku");
                    PolicyContent = PolicyTemplate.Build(Company.FullName, LegalForms.Name(Company.LegalForm.Value), LegalForms.ManagementBody(Company.LegalForm.Value),
                        start.Month == 1 ? "rok kalendarzowy" : $"od {start:dd.MM} do {Parse.Date(YearEnd, "Koniec roku"):dd.MM}", ChartTemplate.Name, HelpVersion());
                }
                break;
            case WizardStep.Polityka:
                if (ApprovePolicy)
                {
                    if (PolicyContent.Contains("[DO UZUPEŁNIENIA", StringComparison.OrdinalIgnoreCase))
                        throw new DomainException("Polityka zawiera pola [DO UZUPEŁNIENIA…] — uzupełnij je albo odznacz zatwierdzenie (zatwierdzisz później w sekcji „Jednostka i polityka”).");
                    if (string.IsNullOrWhiteSpace(PolicyApprovedBy)) throw new DomainException("Podaj osobę zatwierdzającą politykę (kierownik jednostki).");
                }
                break;
            case WizardStep.OsobaOdpowiedzialna:
                if (string.IsNullOrWhiteSpace(ManagerFirstName) || string.IsNullOrWhiteSpace(ManagerLastName))
                    throw new DomainException($"Podaj kierownika jednostki ({LegalForms.ManagementBody(Company.LegalForm.Value)}) — odpowiada za prowadzenie ksiąg (art. 4a UoR).");
                break;
            case WizardStep.KopieZapasowe:
                if (!SkipBackupKey && string.IsNullOrEmpty(RecoveryKey) && Session.StoredBackupKey() is null)
                    throw new DomainException("Wygeneruj klucz kopii zapasowych albo zaznacz, że skonfigurujesz kopie później.");
                if (!string.IsNullOrEmpty(RecoveryKey) && !RecoveryKeySaved) throw new DomainException("Potwierdź, że zapisałeś klucz odzyskiwania poza komputerem.");
                Summary = BuildSummary();
                break;
            case WizardStep.Podsumowanie:
                return;
        }
        Step++;
    });

    private static string HelpVersion() => Sections.HelpViewModel.Version;

    private string BuildSummary() => ConnectToExisting
        ? $"Stanowisko zostanie podłączone do istniejących ksiąg w bazie: {Database.ToProfile().SafeDescription}."
        : $"Baza: {Database.ToProfile().SafeDescription}{Environment.NewLine}" +
          $"Jednostka: {Company.FullName} ({LegalForms.Name(Company.LegalForm.Value)}), NIP {Company.Nip}{Environment.NewLine}" +
          $"Rok obrotowy: {YearStart} – {YearEnd}{(IsFirstYear ? " (pierwszy rok działalności)" : "")}{Environment.NewLine}" +
          $"Plan kont: {ChartTemplate.Name}{(ApproveChart ? ", zatwierdzony" : ", do zatwierdzenia")}{Environment.NewLine}" +
          $"Polityka rachunkowości: {(ApprovePolicy ? $"zatwierdzona przez {PolicyApprovedBy}" : "do uzupełnienia i zatwierdzenia")}{Environment.NewLine}" +
          $"Kierownik jednostki: {ManagerFirstName} {ManagerLastName}{Environment.NewLine}" +
          $"Kopie zapasowe: {(Session.StoredBackupKey() is null ? "nieskonfigurowane" : "klucz zapisany, po utworzeniu ksiąg zostanie wykonana i zweryfikowana pierwsza kopia")}";

    private async Task CheckDatabaseAsync()
    {
        var p = Database.ToProfile();
        if (p.Engine == DatabaseEngine.PostgreSql && !string.IsNullOrEmpty(Database.Password)) Session.Connector.SavePassword(p, Database.Password);
        if (p.Engine == DatabaseEngine.Sqlite) Directory.CreateDirectory(Path.GetDirectoryName(p.SqlitePath!)!);
        var r = await Session.Connector.TestAsync(p);
        if (!r.Success) throw new DomainException(r.Message);
        ConnectToExisting = !r.DatabaseEmpty;
        DatabaseInfo = r.Message + (ConnectToExisting ? " Baza zawiera księgi — stanowisko zostanie do nich podłączone." : " Baza jest pusta — zostaną utworzone nowe księgi.");
        if (ConnectToExisting && !await Dialogs.ConfirmAsync("Istniejące księgi", "Wskazana baza zawiera już księgi. Podłączyć to stanowisko do istniejących ksiąg (bez tworzenia nowej jednostki)?"))
            throw new DomainException("Wskaż inną (pustą) bazę albo inny plik.");
        Session.Config.ActiveDatabase = p;
    }

    [RelayCommand]
    private Task TestDatabaseAsync() => RunAsync(CheckDatabaseAsync);

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
        await using (var db = Session.OpenDb())
        {
            await db.Database.MigrateAsync();
            if (!ConnectToExisting)
            {
                if (await db.Companies.AnyAsync()) throw new DomainException("Baza zawiera już księgi — wróć do kroku wyboru bazy.");
                await CreateBooksAsync(db);
            }
            var actor = OperatorIdentity.For(await db.Companies.Select(c => c.Id).FirstAsync());
            var settings = new SettingsService(db, Session.Clock);
            await settings.SetAsync(null, $"{Terms.SettingKey}.{actor.Login}", new Terms.Acceptance(Terms.Version, actor.Login, Session.Clock.UtcNow));
            await new AuditService(db, Session.Clock).AddAsync(actor, "WARUNKI_ZAAKCEPTOWANE", "Terms", Terms.Version, $"Licencja w wersji {Terms.Version}; stanowisko {Environment.MachineName}");
            await db.SaveChangesAsync();
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

    private async Task CreateBooksAsync(KsiegiDbContext db)
    {
        var setup = new SetupService(db, Session.Clock);
        var company = await setup.CreateCompanyAsync(OperatorIdentity.For(Guid.Empty), Company.ToCompany());
        var actor = OperatorIdentity.For(company.Id);
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
