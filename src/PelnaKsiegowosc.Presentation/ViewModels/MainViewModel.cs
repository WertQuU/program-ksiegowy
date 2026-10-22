using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels.Sections;

namespace PelnaKsiegowosc.Presentation.ViewModels;

/// <summary>Okno główne: menu sekcji, wybór roku obrotowego, pasek stanu (baza, operator, kopie).</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    public MainViewModel(AppSession session, IDialogService dialogs) : base(dialogs)
    {
        Session = session;
        Sections = new List<SectionViewModel>
        {
            new DashboardViewModel(session, dialogs),
            new CompanyViewModel(session, dialogs),
            new DocumentsViewModel(session, dialogs),
            new SalesViewModel(session, dialogs),
            new PurchasesViewModel(session, dialogs),
            new LedgerViewModel(session, dialogs),
            new ChartViewModel(session, dialogs),
            new CounterpartiesViewModel(session, dialogs),
            new BankViewModel(session, dialogs),
            new VatViewModel(session, dialogs),
            new CitViewModel(session, dialogs),
            new FixedAssetsViewModel(session, dialogs),
            new CapitalViewModel(session, dialogs),
            new ReportsViewModel(session, dialogs),
            new ClosingViewModel(session, dialogs),
            new ArchiveViewModel(session, dialogs),
            new BackupsViewModel(session, dialogs),
            new IntegrationsViewModel(session, dialogs),
            new SettingsViewModel(session, dialogs),
            new HelpViewModel(session, dialogs)
        };
        session.ContextChanged += (_, _) => RefreshHeader();
        RefreshHeader();
    }

    public AppSession Session { get; }
    public IReadOnlyList<SectionViewModel> Sections { get; }

    [ObservableProperty] private SectionViewModel? _selectedSection;
    [ObservableProperty] private string _title = "Pełna Księgowość";
    [ObservableProperty] private string _companyLine = "";
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private List<Choice<Guid>> _years = new();
    [ObservableProperty] private Choice<Guid>? _selectedYear;
    private bool _updatingYears;

    public async Task StartAsync()
    {
        SelectedSection = Sections[0];
        await Sections[0].LoadAsync();
    }

    partial void OnSelectedSectionChanged(SectionViewModel? value)
    {
        if (value is { Loaded: false }) _ = value.LoadAsync();
    }

    partial void OnSelectedYearChanged(Choice<Guid>? value)
    {
        if (_updatingYears || value is null || value.Value == Session.Year?.Id) return;
        Session.SelectYear(value.Value);
        _ = SelectedSection?.LoadAsync();
    }

    private void RefreshHeader()
    {
        var c = Session.Company;
        Title = c is null ? "Pełna Księgowość" : $"{c.ShortName} — Pełna Księgowość";
        CompanyLine = c is null ? "" : $"{c.FullName} • {LegalForms.Name(c.LegalForm)} • NIP {c.Nip}";
        _updatingYears = true;
        Years = Session.Years.Select(y => new Choice<Guid>(y.Id, $"Rok {y.Name} ({Display.Enum(y.Status)})")).ToList();
        SelectedYear = Years.FirstOrDefault(y => y.Value == Session.Year?.Id);
        _updatingYears = false;
        var b = Session.Config.Backup;
        Footer = $"Baza: {Session.DatabaseDescription}   •   Operator: {Session.User.Login}   •   " +
                 (b.LastSuccessUtc is { } t ? $"Ostatnia kopia: {t.ToPlDateTime()}" : "Brak kopii zapasowej");
        Warning = Session.Profile.IsEmergencyWorkingCopy
            ? "TRYB AWARYJNY: pracujesz na osobnej kopii roboczej. Zmiany wymagają późniejszego uzgodnienia z bazą główną."
            : Session.StoredBackupKey() is null ? "Brak klucza kopii zapasowych na tym stanowisku — kopie automatyczne są wyłączone (sekcja „Backupy”)." : null;
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await Session.RefreshAsync();
        if (SelectedSection is not null) await SelectedSection.LoadAsync();
    });

    /// <summary>Kopia automatyczna (wywoływana cyklicznie przez aplikację) — błędy zapisywane w konfiguracji i logu, bez okien.</summary>
    public async Task AutomaticBackupAsync()
    {
        if (!Session.AutomaticBackupDue()) return;
        try
        {
            await Session.BackupAsync("automatyczna", upload: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            Session.Config.Backup.LastError = ex.Message;
            Session.SaveConfig();
        }
        RefreshHeader();
    }

    /// <summary>Kopia przy zamykaniu programu (jeżeli włączona i jest klucz).</summary>
    public async Task BackupOnExitAsync()
    {
        if (!Session.Config.Backup.BackupOnExit || Session.StoredBackupKey() is null || Session.Company is null) return;
        try { await Session.BackupAsync("przy zamknięciu programu", upload: true); }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }
}
