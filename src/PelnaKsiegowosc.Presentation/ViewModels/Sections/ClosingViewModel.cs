using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Application.Periods;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Zamknięcie miesiąca (lista kontrolna), ponowne otwarcie z uzasadnieniem, zamknięcie roku z bilansem otwarcia.</summary>
public sealed partial class ClosingViewModel : SectionViewModel
{
    public ClosingViewModel(AppSession s, IDialogService d) : base(s, d, "Zamknięcie okresu i roku", "🔒")
    {
        MonthClose = new MonthCloseViewModel(s, d);
        Valuation = new ValuationViewModel(s, d);
        YearClose = new YearCloseViewModel(s, d);
    }

    public MonthCloseViewModel MonthClose { get; }
    public ValuationViewModel Valuation { get; }
    public YearCloseViewModel YearClose { get; }

    [ObservableProperty] private TableData _periods = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedPeriod;
    [ObservableProperty] private TableData _checklist = TableData.Empty;
    [ObservableProperty] private string _yearInfo = "";

    private static TableData ToTable(CloseChecklist c)
    {
        var t = new TableData(new TableColumn("Kontrola", Width: 2), new TableColumn("Wynik", Width: 0.6), new TableColumn("Waga", Width: 1), new TableColumn("Szczegóły", Width: 3));
        foreach (var i in c.Items) t.Add(null, i.Title, i.Passed ? "✔" : "✖", i.Severity, i.Details);
        return t;
    }

    protected override async Task LoadCoreAsync()
    {
        await MonthClose.RefreshAsync();
        await Valuation.RefreshAsync();
        await YearClose.RefreshAsync();
        var y = Session.CurrentYear;
        var t = new TableData(new TableColumn("Okres", Width: 0.6), new TableColumn("Od", ColumnKind.Date), new TableColumn("Do", ColumnKind.Date), new TableColumn("Status"),
            new TableColumn("Zamknięto", ColumnKind.Text, 1.2), new TableColumn("Ponowne otwarcia", ColumnKind.Integer, 0.6));
        foreach (var p in y.Periods) t.Add(p.Id, p.Name, p.StartDate, p.EndDate, p.Status, p.ClosedAtUtc is { } c ? c.ToPlDateTime() : "", p.ReopenCount);
        Periods = t;
        YearInfo = $"Rok obrotowy {y.Name}: {Display.Enum(y.Status)}" + (y.ClosedAtUtc is { } at ? $", zamknięty {at.ToPlDateTime()}" : "");
    }

    private Guid PeriodId => SelectedPeriod?.Tag is Guid g ? g : throw new DomainException("Zaznacz okres.");

    [RelayCommand]
    private Task CheckAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        Checklist = ToTable(await new PeriodService(db, Session.User, Session.Clock).BuildChecklistAsync(PeriodId));
    });

    [RelayCommand]
    private Task ClosePeriodAsync() => ChangeAsync(async () =>
    {
        var id = PeriodId;
        await using var db = Session.OpenDb();
        var svc = new PeriodService(db, Session.User, Session.Clock);
        var c = await svc.BuildChecklistAsync(id);
        Checklist = ToTable(c);
        if (c.HasBlocking) throw new DomainException("Okresu nie można zamknąć — usuń błędy blokujące z listy kontrolnej.");
        string? ack = null;
        if (c.NeedsAcknowledgement)
        {
            ack = await Dialogs.PromptAsync("Zamknięcie okresu", "Lista kontrolna zawiera ostrzeżenia wymagające potwierdzenia. Podaj uzasadnienie zamknięcia mimo ostrzeżeń:");
            if (string.IsNullOrWhiteSpace(ack)) return;
        }
        else if (!await Dialogs.ConfirmAsync("Zamknięcie okresu", $"Zamknąć okres {SelectedPeriod!.Cells[0]}? Księgowanie z datą w zamkniętym okresie będzie zablokowane.")) return;
        await svc.ClosePeriodAsync(id, ack);
        await Session.RefreshAsync();
    }, "Okres zamknięty.");

    [RelayCommand]
    private Task ReopenPeriodAsync() => ChangeAsync(async () =>
    {
        var id = PeriodId;
        var reason = await Dialogs.PromptAsync("Ponowne otwarcie okresu", "Otwarcie zamkniętego okresu jest rejestrowane w historii i dzienniku audytu. Podaj uzasadnienie:");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await using var db = Session.OpenDb();
        await new PeriodService(db, Session.User, Session.Clock).ReopenPeriodAsync(id, reason.Trim());
        await Session.RefreshAsync();
    }, "Okres ponownie otwarty.");

    [RelayCommand]
    private Task PreviewYearCloseAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var p = await new PeriodService(db, Session.User, Session.Clock).PreviewYearCloseAsync(Session.CurrentYear.Id);
        Checklist = ToTable(p.Checklist);
        await Dialogs.ShowTextAsync("Podgląd zamknięcia roku", $"Wynik finansowy: {Money.Format(p.NetResult)} zł ({(p.NetResult >= 0 ? "zysk" : "strata")}){Environment.NewLine}{Environment.NewLine}" +
            "Zapis zamknięcia (przeksięgowanie kont wynikowych):" + Environment.NewLine +
            string.Join(Environment.NewLine, p.ClosingLines.Select(l => $"  {l.AccountNumber,-12} Wn {Money.Format(l.Debit),14}  Ma {Money.Format(l.Credit),14}  {l.Description}")));
    });

    /// <summary>Dokumentacja sytuacji szczególnej — bez obejścia zatwierdzenia sprawozdania finansowego.</summary>
    [RelayCommand]
    private Task FinalCloseExceptionAsync() => ChangeAsync(async () =>
    {
        var y = Session.CurrentYear;
        var liquidation = await Dialogs.ConfirmAsync("Dokumentacja okoliczności szczególnych",
            "Ten zapis NIE odblokowuje standardowego zamknięcia ksiąg bez zatwierdzonego sprawozdania (art. 12 ust. 4 UoR). Likwidacja i podobne zdarzenia wymagają odrębnego trybu zamknięcia na właściwy dzień. Czy dokument dotyczy likwidacji/zakończenia działalności? Tak — takie zdarzenie; Nie — sprawozdanie niezatwierdzone w terminie.");
        var document = await Dialogs.PromptAsync("Tryb wyjątkowy zamknięcia", "Dokument źródłowy (np. uchwała o likwidacji, postanowienie sądu, protokół zgromadzenia):", "");
        if (string.IsNullOrWhiteSpace(document)) return;
        var reason = await Dialogs.PromptAsync("Tryb wyjątkowy zamknięcia", "Okoliczności i podstawa prawna (zapis w dzienniku zdarzeń; program nie potwierdza dopuszczalności tego trybu):", "");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await using var db = Session.OpenDb();
        await new PeriodService(db, Session.User, Session.Clock).RecordFinalCloseExceptionAsync(y.Id,
            liquidation ? PeriodService.FinalCloseExceptionBasis.ZakonczenieDzialalnosci : PeriodService.FinalCloseExceptionBasis.NiezatwierdzoneWTerminie, document, reason);
    }, "Zarejestrowano okoliczności szczególne (bez odblokowania zamknięcia).");

    [RelayCommand]
    private Task CloseYearAsync() => ChangeAsync(async () =>
    {
        var y = Session.CurrentYear;
        if (y.Status == FiscalYearStatus.Zamkniety) throw new DomainException("Rok jest już zamknięty.");
        await using var db = Session.OpenDb();
        var svc = new PeriodService(db, Session.User, Session.Clock);
        var p = await svc.PreviewYearCloseAsync(y.Id);
        Checklist = ToTable(p.Checklist);
        if (p.Checklist.HasBlocking) throw new DomainException("Roku nie można zamknąć — usuń błędy blokujące z listy kontrolnej.");
        string? ack = null;
        if (p.Checklist.NeedsAcknowledgement)
        {
            ack = await Dialogs.PromptAsync("Zamknięcie roku", "Lista kontrolna zawiera ostrzeżenia. Podaj uzasadnienie zamknięcia mimo ostrzeżeń:");
            if (string.IsNullOrWhiteSpace(ack)) return;
        }
        var confirmation = await Dialogs.PromptAsync("Zamknięcie roku — operacja nieodwracalna",
            $"Zamknięcie roku przeksięguje konta wynikowe na wynik finansowy, zablokuje księgi roku i utworzy bilans otwarcia roku następnego.{Environment.NewLine}" +
            $"Przed zamknięciem zostanie wykonana kopia zapasowa.{Environment.NewLine}Wymagane jest zatwierdzone sprawozdanie finansowe. Aby potwierdzić, wpisz: ZAMKNIJ {y.Name}");
        if (confirmation is null) return;
        if (Session.StoredBackupKey() is not null) await Session.BackupAsync($"przed zamknięciem roku {y.Name}", upload: true);
        else if (!await Dialogs.ConfirmAsync("Brak kopii zapasowej", "Na tym stanowisku nie ma klucza kopii zapasowych — kopia przed zamknięciem nie zostanie wykonana. Kontynuować?")) return;
        var next = await svc.CloseYearAsync(y.Id, confirmation.Trim(), ack);
        await Session.RefreshAsync();
        Session.SelectYear(next.Id);
        await Dialogs.InfoAsync("Zamknięcie roku", $"Rok {y.Name} zamknięty. Utworzono rok {next.Name} z planem kont i bilansem otwarcia.");
    }, "Rok obrotowy zamknięty.");
}
