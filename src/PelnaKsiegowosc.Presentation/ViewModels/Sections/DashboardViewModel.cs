using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Pozycja listy „Do zrobienia” z przejściem do miejsca, w którym usuwa się problem.</summary>
public sealed class IssueItem
{
    public IssueItem(Issue issue, AppSession session)
    {
        Issue = issue;
        GoCommand = new RelayCommand(() => session.Navigate(issue.Area));
    }

    public Issue Issue { get; }
    public string Title => Issue.Title;
    public string Details => Issue.Details;
    public string HowToFix => "Co zrobić: " + Issue.HowToFix;
    public bool HasDetails => !string.IsNullOrWhiteSpace(Issue.Details);
    public string Badge => Issue.Severity switch { IssueSeverity.Blad => "BŁĄD", IssueSeverity.Ostrzezenie => "UWAGA", _ => "WSKAZÓWKA" };
    public string Color => Issue.Severity switch { IssueSeverity.Blad => "#C62828", IssueSeverity.Ostrzezenie => "#E08A00", _ => "#1565C0" };
    public IRelayCommand GoCommand { get; }
}

public sealed class DeadlineItem
{
    public DeadlineItem(Deadline d, DateOnly today, AppSession session)
    {
        Deadline = d;
        DateText = d.Date.ToPl();
        Status = d.Done ? "zrobione" : d.IsOverdue(today) ? $"po terminie ({-d.DaysLeft(today)} dni)" : d.DaysLeft(today) == 0 ? "dziś"
            : d.DaysLeft(today) < 0 ? "termin minął — sprawdź, czy obowiązek wykonano" : $"za {d.DaysLeft(today)} dni";
        Color = d.Done ? "#2E7D32" : d.IsOverdue(today) ? "#C62828" : d.DaysLeft(today) <= 5 ? "#E08A00" : "#546E7A";
        GoCommand = new RelayCommand(() => session.Navigate(d.Area));
    }

    public Deadline Deadline { get; }
    public string DateText { get; }
    public string Title => Deadline.Title;
    public string Details => Deadline.Details;
    public string Status { get; }
    public string Color { get; }
    public IRelayCommand GoCommand { get; }
}

/// <summary>Pulpit: co wymaga uwagi (asystent kontroli), najbliższe terminy, kluczowe liczby i szybkie czynności.</summary>
public sealed partial class DashboardViewModel : SectionViewModel
{
    public DashboardViewModel(AppSession s, IDialogService d) : base(s, d, "Pulpit", "⌂") { }

    [ObservableProperty] private List<IssueItem> _issues = new();
    [ObservableProperty] private List<DeadlineItem> _deadlines = new();
    [ObservableProperty] private string _healthTitle = "";
    [ObservableProperty] private string _healthColor = "#2E7D32";
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private string _receivables = "";
    [ObservableProperty] private string _payables = "";
    [ObservableProperty] private string _moneyText = "";
    [ObservableProperty] private string _nextDeadline = "";
    [ObservableProperty] private string _backupInfo = "";
    [ObservableProperty] private bool _showHints = true;

    private List<Issue> _all = new();

    partial void OnShowHintsChanged(bool value) => Filter();

    [ObservableProperty] private bool _noIssues;

    private void Filter()
    {
        Issues = _all.Where(i => ShowHints || i.Severity != IssueSeverity.Wskazowka).Select(i => new IssueItem(i, Session)).ToList();
        NoIssues = Issues.Count == 0;
    }

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var assistant = new BookkeepingAssistant(db, Session.User, Session.Clock);
        _all = await assistant.CheckAsync(Session.CurrentYear.Id, Session.Config.Backup.LastSuccessUtc);
        Filter();
        var errors = _all.Count(i => i.Severity == IssueSeverity.Blad);
        var warnings = _all.Count(i => i.Severity == IssueSeverity.Ostrzezenie);
        (HealthTitle, HealthColor, HealthText) = errors > 0
            ? ($"Do poprawy: {errors}", "#C62828", $"Wykryto błędy wymagające poprawy ({errors}) i ostrzeżenia ({warnings}). Zacznij od pozycji oznaczonych „BŁĄD”.")
            : warnings > 0
                ? ($"Do sprawdzenia: {warnings}", "#E08A00", $"Brak błędów. {warnings} spraw wymaga uwagi.")
                : ("Wszystko w porządku", "#2E7D32", "Asystent nie wykrył błędów ani zaległości.");

        var today = Session.Clock.Today;
        var all = await assistant.DeadlinesAsync();
        Deadlines = all.Where(x => !x.Done || x.Date >= today.AddDays(-10)).Where(x => x.Date <= today.AddDays(45))
            .Select(x => new DeadlineItem(x, today, Session)).ToList();
        var next = all.FirstOrDefault(x => !x.Done && x.Date >= today);
        NextDeadline = next is null ? "brak w ciągu 60 dni" : $"{next.Date.ToPl()} — {next.Title}";

        var items = await db.OpenItems.AsNoTracking().Where(o => o.CompanyId == Session.CompanyId && o.Status != OpenItemStatus.Rozliczona).ToListAsync();
        var rec = items.Where(i => i.Side == OpenItemSide.Naleznosc).ToList();
        var pay = items.Where(i => i.Side == OpenItemSide.Zobowiazanie).ToList();
        Receivables = $"{Money.Format(rec.Sum(i => i.RemainingPln))} zł" + (rec.Any(i => i.DaysOverdue(today) > 0) ? $"\nprzeterminowane: {Money.Format(rec.Where(i => i.DaysOverdue(today) > 0).Sum(i => i.RemainingPln))} zł" : "");
        Payables = $"{Money.Format(pay.Sum(i => i.RemainingPln))} zł" + (pay.Any(i => i.DaysOverdue(today) > 0) ? $"\nprzeterminowane: {Money.Format(pay.Where(i => i.DaysOverdue(today) > 0).Sum(i => i.RemainingPln))} zł" : "");

        var moneyAccounts = await db.MoneyAccounts.AsNoTracking().Where(m => m.CompanyId == Session.CompanyId && m.IsActive).ToListAsync();
        var tb = await new Application.Ledger.LedgerQueries(db).TrialBalanceAsync(Session.CurrentYear.Id, Session.CurrentYear.StartDate,
            today < Session.CurrentYear.EndDate ? (today < Session.CurrentYear.StartDate ? Session.CurrentYear.StartDate : today) : Session.CurrentYear.EndDate);
        MoneyText = string.Join("\n", moneyAccounts.Select(m => $"{(m.Kind == MoneyAccountKind.Kasa ? "Kasa" : m.Name)}: {Money.Format(tb.FirstOrDefault(t => t.Account.Number == m.LedgerAccountNumber)?.Balance ?? 0)} zł{(m.Currency != "PLN" ? $" (rachunek {m.Currency}, wartość w PLN)" : "")}"));
        if (string.IsNullOrEmpty(MoneyText)) MoneyText = "brak rachunków";

        var b = Session.Config.Backup;
        BackupInfo = b.LastSuccessUtc is { } last ? $"Ostatnia kopia: {last.ToPlDateTime()}" : "Brak kopii zapasowej";
    }

    [RelayCommand] private void NewSale() => Session.Navigate(AppArea.Sprzedaz, "nowy");
    [RelayCommand] private void NewPurchase() => Session.Navigate(AppArea.Zakupy, "nowy");
    [RelayCommand] private void ImportStatement() => Session.Navigate(AppArea.Bank);
    [RelayCommand] private void CloseMonth() => Session.Navigate(AppArea.Zamkniecie);
    [RelayCommand] private void OpenReports() => Session.Navigate(AppArea.Raporty);

    [RelayCommand]
    private Task BackupNowAsync() => ChangeAsync(async () =>
    {
        var r = await Session.BackupAsync("ręczna (pulpit)", upload: true);
        await Dialogs.InfoAsync("Kopia zapasowa", $"Utworzono i zweryfikowano kopię:{Environment.NewLine}{r.FilePath}" +
            (r.RemoteKey is not null ? $"{Environment.NewLine}Wysłano do Object Storage: {r.RemoteKey}" : "") +
            (r.RemoteError is not null ? $"{Environment.NewLine}Błąd wysyłki: {r.RemoteError}" : ""));
    }, "Kopia zapasowa wykonana.");
}
