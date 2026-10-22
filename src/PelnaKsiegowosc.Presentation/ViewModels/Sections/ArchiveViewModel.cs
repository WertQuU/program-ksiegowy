using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Archiwum: repozytorium załączników z kontrolą sum SHA-256, dziennik zdarzeń (audyt) z kontrolą łańcucha skrótów, lata zamknięte.</summary>
public sealed partial class ArchiveViewModel : SectionViewModel
{
    public ArchiveViewModel(AppSession s, IDialogService d) : base(s, d, "Archiwum", "🗄") { }

    [ObservableProperty] private TableData _attachments = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedAttachment;
    [ObservableProperty] private TableData _audit = TableData.Empty;
    [ObservableProperty] private TableData _closedYears = TableData.Empty;
    [ObservableProperty] private string _auditFilter = "";

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var docs = await db.Documents.AsNoTracking().Where(d => d.CompanyId == Session.CompanyId).Select(d => new { d.Id, d.Number }).ToDictionaryAsync(d => d.Id, d => d.Number);
        var at = new TableData(new TableColumn("Plik", Width: 2), new TableColumn("Dokument", Width: 1.3), new TableColumn("Rozmiar (B)", ColumnKind.Integer, 0.7),
            new TableColumn("Dodano", ColumnKind.Text, 1.1), new TableColumn("SHA-256", Width: 2.5), new TableColumn("Kopia w chmurze", Width: 1));
        foreach (var a in await db.Attachments.AsNoTracking().Where(a => a.CompanyId == Session.CompanyId).OrderByDescending(a => a.CreatedAtUtc).Take(5000).ToListAsync())
            at.Add(a.Id, a.FileName, a.DocumentId is { } d ? docs.GetValueOrDefault(d) : "", a.SizeBytes, a.CreatedAtUtc.ToPlDateTime(), a.Sha256, a.ArchivedToCloudAtUtc?.ToPlDateTime() ?? "");
        Attachments = at;

        var q = db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Sequence).AsQueryable();
        if (!string.IsNullOrWhiteSpace(AuditFilter)) q = q.Where(e => e.Action.Contains(AuditFilter.Trim().ToUpper()) || e.EntityType.Contains(AuditFilter.Trim()));
        var au = new TableData(new TableColumn("Nr", ColumnKind.Integer, 0.5), new TableColumn("Czas", Width: 1.1), new TableColumn("Operator", Width: 1.3), new TableColumn("Czynność", Width: 1.5),
            new TableColumn("Obiekt", Width: 1), new TableColumn("Szczegóły", Width: 3.5), new TableColumn("Stanowisko", Width: 0.9));
        foreach (var e in await q.Take(2000).ToListAsync()) au.Add(e.Id, e.Sequence, e.AtUtc.ToPlDateTime(), e.UserLogin, e.Action, e.EntityType, e.Details, e.MachineName);
        Audit = au;

        var cy = new TableData(new TableColumn("Rok"), new TableColumn("Od", ColumnKind.Date), new TableColumn("Do", ColumnKind.Date), new TableColumn("Status"), new TableColumn("Zamknięty", Width: 1.2),
            new TableColumn("Przechowywać co najmniej do", ColumnKind.Date));
        foreach (var y in Session.Years.Where(y => y.Status != Domain.Periods.FiscalYearStatus.Otwarty))
            cy.Add(y.Id, y.Name, y.StartDate, y.EndDate, y.Status, y.ClosedAtUtc?.ToPlDateTime() ?? "", new DateOnly(y.EndDate.Year + 6, 1, 1).AddDays(-1));
        ClosedYears = cy;
    }

    [RelayCommand] private Task FilterAuditAsync() => LoadAsync();

    [RelayCommand]
    private Task VerifyAuditChainAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var problems = await new AuditService(db, Session.Clock).VerifyChainAsync();
        await Dialogs.ShowTextAsync("Kontrola dziennika zdarzeń", problems.Count == 0 ? "Łańcuch skrótów dziennika zdarzeń jest nienaruszony." : string.Join(Environment.NewLine, problems));
    });

    [RelayCommand]
    private Task VerifyAttachmentsAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        var problems = new List<string>();
        var list = await db.Attachments.AsNoTracking().Where(a => a.CompanyId == Session.CompanyId).ToListAsync();
        foreach (var a in list)
        {
            try { await svc.ReadAttachmentAsync(a.Id, Session.Attachments); }
            catch (Exception ex) when (ex is DomainException or IOException) { problems.Add($"{a.FileName}: {ex.Message}"); }
        }
        await Dialogs.ShowTextAsync("Kontrola załączników", problems.Count == 0 ? $"Sprawdzono {list.Count} załączników — sumy SHA-256 zgodne." : string.Join(Environment.NewLine, problems));
    });

    [RelayCommand]
    private Task SaveAttachmentAsync() => RunAsync(async () =>
    {
        if (SelectedAttachment?.Tag is not Guid id) throw new DomainException("Zaznacz załącznik.");
        var target = await Dialogs.SaveFileAsync("Zapisz załącznik", SelectedAttachment.Cells[0]);
        if (target is null) return;
        await using var db = Session.OpenDb();
        await File.WriteAllBytesAsync(target, await new DocumentService(db, Session.User, Session.Clock).ReadAttachmentAsync(id, Session.Attachments));
        StatusMessage = $"Zapisano {target}";
    });
}
