using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

public sealed partial class DashboardViewModel : SectionViewModel
{
    public DashboardViewModel(AppSession s, IDialogService d) : base(s, d, "Pulpit", "⌂") { }

    [ObservableProperty] private TableData _readiness = TableData.Empty;
    [ObservableProperty] private TableData _summary = TableData.Empty;
    [ObservableProperty] private string _backupInfo = "";

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var companyId = Session.CompanyId;
        var ready = new TableData(new TableColumn("Wymaganie", Width: 2), new TableColumn("Stan", Width: 0.6), new TableColumn("Szczegóły", Width: 3));
        foreach (var (item, ok, details) in await new SetupService(db, Session.Clock).ReadinessAsync(companyId))
            ready.Add(null, item, ok ? "✔ spełnione" : "✖ brak", details);
        Readiness = ready;

        var year = Session.CurrentYear;
        var today = Session.Clock.Today;
        var sum = new TableData(new TableColumn("Wskaźnik", Width: 2.5), new TableColumn("Wartość", ColumnKind.Text, 1.2));
        sum.Add(null, "Rok obrotowy", $"{year.Name} ({Display.Enum(year.Status)})");
        sum.Add(null, "Okresy otwarte", year.Periods.Count(p => p.Status == PeriodStatus.Otwarty).ToString());
        sum.Add(null, "Dokumenty robocze", (await db.Documents.CountAsync(x => x.CompanyId == companyId && x.Status == DocumentStatus.Roboczy)).ToString());
        sum.Add(null, "Dokumenty zatwierdzone, niezaksięgowane", (await db.Documents.CountAsync(x => x.CompanyId == companyId && x.Status == DocumentStatus.Zatwierdzony)).ToString());
        sum.Add(null, "Zapisy w buforze", (await db.JournalEntries.CountAsync(x => x.FiscalYearId == year.Id && x.Status == EntryStatus.Bufor)).ToString());
        sum.Add(null, "Operacje bankowe nierozliczone", (await db.MoneyTransactions.CountAsync(x => x.CompanyId == companyId && x.Status == TransactionStatus.Nierozliczona)).ToString());
        var items = await db.OpenItems.AsNoTracking().Where(o => o.CompanyId == companyId && o.Status != OpenItemStatus.Rozliczona).ToListAsync();
        sum.Add(null, "Należności nierozliczone (PLN)", Money.Format(items.Where(i => i.Side == OpenItemSide.Naleznosc).Sum(i => i.RemainingPln)));
        sum.Add(null, "  w tym przeterminowane", Money.Format(items.Where(i => i.Side == OpenItemSide.Naleznosc && i.DaysOverdue(today) > 0).Sum(i => i.RemainingPln)));
        sum.Add(null, "Zobowiązania nierozliczone (PLN)", Money.Format(items.Where(i => i.Side == OpenItemSide.Zobowiazanie).Sum(i => i.RemainingPln)));
        sum.Add(null, "  w tym przeterminowane", Money.Format(items.Where(i => i.Side == OpenItemSide.Zobowiazanie && i.DaysOverdue(today) > 0).Sum(i => i.RemainingPln)));
        Summary = sum;

        var b = Session.Config.Backup;
        BackupInfo = (b.LastSuccessUtc is { } last ? $"Ostatnia kopia zapasowa: {last.ToPlDateTime()}" : "Nie wykonano jeszcze kopii zapasowej.")
                     + (Session.StoredBackupKey() is null ? " Brak klucza kopii na tym stanowisku." : "")
                     + (b.LastError is { } e ? $" Ostatni błąd wysyłki: {e}" : "");
    }

    [RelayCommand]
    private Task BackupNowAsync() => ChangeAsync(async () =>
    {
        var r = await Session.BackupAsync("ręczna (pulpit)", upload: true);
        await Dialogs.InfoAsync("Kopia zapasowa", $"Utworzono i zweryfikowano kopię:{Environment.NewLine}{r.FilePath}" +
            (r.RemoteKey is not null ? $"{Environment.NewLine}Wysłano do Object Storage: {r.RemoteKey}" : "") +
            (r.RemoteError is not null ? $"{Environment.NewLine}Błąd wysyłki: {r.RemoteError}" : ""));
    }, "Kopia zapasowa wykonana.");
}
