using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Infrastructure.Integrity;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Presentation.ViewModels.Sections;

using AA = PelnaKsiegowosc.Application.Assistant.AppArea;

namespace PelnaKsiegowosc.Presentation.ViewModels;

public sealed record MenuHeader(string Text);

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
            new HelpViewModel(session, dialogs),
            new RegulationsViewModel(session, dialogs),
            new PayrollViewModel(session, dialogs),
            new DeclarationsViewModel(session, dialogs),
            new StockViewModel(session, dialogs)
        };
        SectionViewModel S(PelnaKsiegowosc.Application.Assistant.AppArea a) => Sections[(int)a];
        _fullMenu = new List<object>
        {
            new MenuHeader("NA CO DZIEŃ"),
            S(AA.Pulpit), S(AA.Sprzedaz), S(AA.Zakupy), S(AA.Bank), S(AA.Kontrahenci),
            new MenuHeader("ROZLICZENIA"),
            S(AA.Zamkniecie), S(AA.Vat), S(AA.Cit), S(AA.Place), S(AA.Deklaracje), S(AA.Raporty),
            new MenuHeader("KSIĘGI"),
            S(AA.Dokumenty), S(AA.Ksiegowosc), S(AA.PlanKont), S(AA.SrodkiTrwale), S(AA.Magazyn), S(AA.Kapital),
            new MenuHeader("FIRMA I PROGRAM"),
            S(AA.Jednostka), S(AA.Przepisy), S(AA.Backupy), S(AA.Archiwum), S(AA.Integracje), S(AA.Ustawienia), S(AA.Pomoc)
        };
        session.ContextChanged += (_, _) => RefreshHeader();
        session.NavigationRequested += (area, action) => _ = NavigateAsync(area, action);
        RefreshHeader();
    }

    public AppSession Session { get; }
    public IReadOnlyList<SectionViewModel> Sections { get; }

    private readonly List<object> _fullMenu;

    /// <summary>Menu z nagłówkami grup (nagłówki nie są wybieralne).</summary>
    public IReadOnlyList<object> MenuItems => _fullMenu;

    public object? SelectedMenuItem
    {
        get => SelectedSection;
        set
        {
            if (value is SectionViewModel s) SelectedSection = s;
            else OnPropertyChanged();
        }
    }

    [ObservableProperty] private SectionViewModel? _selectedSection;
    [ObservableProperty] private string _title = "Pełna Księgowość";
    [ObservableProperty] private string _companyLine = "";
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string? _sharedChangesNotice;
    [ObservableProperty] private string? _integrityNotice;
    private bool _checkingLocalIntegrity;
    private long? _lastSeenSharedSequence;
    private bool _pollingSharedChanges;
    [ObservableProperty] private List<Choice<Guid>> _years = new();
    [ObservableProperty] private Choice<Guid>? _selectedYear;
    private bool _updatingYears;

    public async Task StartAsync()
    {
        SelectedSection = Sections[0];
        await Sections[0].LoadAsync();
    }

    /// <summary>Przejście do sekcji (kolejność sekcji odpowiada wyliczeniu AppArea) i opcjonalnie rozpoczęcie czynności.</summary>
    public async Task NavigateAsync(PelnaKsiegowosc.Application.Assistant.AppArea area, string? action)
    {
        var section = Sections[(int)area];
        SelectedSection = section;
        if (!section.Loaded) await section.LoadAsync();
        if (action == "nowy")
        {
            if (section is SalesViewModel s) await s.NewCommand.ExecuteAsync(null);
            else if (section is PurchasesViewModel p) await p.NewCommand.ExecuteAsync(null);
            else if (section is LedgerViewModel l) await l.NewEntryCommand.ExecuteAsync(null);
        }
    }

    partial void OnSelectedSectionChanged(SectionViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedMenuItem));
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

    private async Task<long> ReadSharedSequenceAsync(CancellationToken ct = default)
    {
        await using var db = Session.OpenDb();
        return await db.AuditEvents.AsNoTracking()
            .Where(a => a.CompanyId == Session.CompanyId || a.CompanyId == null)
            .MaxAsync(a => (long?)a.Sequence, ct) ?? 0L;
    }

    /// <summary>
    /// Kilka instalacji korzysta z jednej bazy serwerowej, nie wymienia lokalnych baz.
    /// Monitor zmian sygnalizuje nowsze zapisy innych operatorów. Nie przeładowuje
    /// automatycznie edytowanych formularzy, aby nie utracić niezapisanej pracy.
    /// </summary>
    public async Task CheckSharedChangesAsync(CancellationToken ct = default)
    {
        if (_pollingSharedChanges || Session.Profile.Engine != DatabaseEngine.PostgreSql || Session.Company is null) return;
        _pollingSharedChanges = true;
        try
        {
            var current = await ReadSharedSequenceAsync(ct);
            if (_lastSeenSharedSequence is { } last && current > last)
                SharedChangesNotice = "Wspólna baza została zaktualizowana. Kliknij „Odśwież”, aby pobrać aktualne dane; sprawdź niezapisane zmiany przed odświeżeniem.";
            else if (_lastSeenSharedSequence is null)
                _lastSeenSharedSequence = current;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Błąd sieci nie powoduje przełączenia na lokalną kopię ani utraty edytowanych danych.
            SharedChangesNotice = "Nie udało się sprawdzić zmian wspólnej bazy. Sprawdź połączenie przed zapisem.";
            ErrorLog.Write(ex);
        }
        finally { _pollingSharedChanges = false; }
    }


    /// <summary>
    /// Pomocniczy SQLite zawiera tylko lokalne odciski SHA-256 zapisów zatwierdzonych
    /// w PostgreSQL. Ostrzeżenia nie blokują księgowania ani nawigacji.
    /// </summary>
    public async Task CheckLocalIntegrityAsync(CancellationToken ct = default)
    {
        if (_checkingLocalIntegrity || Session.Profile.Engine != DatabaseEngine.PostgreSql || Session.Company is null) return;
        _checkingLocalIntegrity = true;
        try
        {
            await using var db = Session.OpenDb();
            var mirror = new LocalLedgerMirror(Session.Paths.DataDirectory, Session.Profile, Session.CompanyId);
            var result = await mirror.CompareAsync(db, Session.CompanyId, ct);
            IntegrityNotice = result.Status switch
            {
                LocalLedgerMirror.MirrorStatus.Divergence =>
                    "OSTRZEŻENIE — kontrola integralności: " + result.Message +
                    (result.Problems.Count > 0 ? " Pierwsza niezgodność: " + result.Problems[0] : ""),
                LocalLedgerMirror.MirrorStatus.BaselineCreated => "Kontrola integralności: " + result.Message,
                _ => null
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            IntegrityNotice = "Nie można wykonać niezależnej kontroli integralności. Sprawdź połączenie z serwerem i lokalny plik kontrolny; program pozostaje dostępny.";
        }
        finally { _checkingLocalIntegrity = false; }
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await Session.RefreshAsync();
        if (SelectedSection is not null) await SelectedSection.LoadAsync();
        if (Session.Profile.Engine == DatabaseEngine.PostgreSql)
        {
            _lastSeenSharedSequence = await ReadSharedSequenceAsync();
            SharedChangesNotice = null;
        }
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
