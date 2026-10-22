using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Dziennik: zapisy w buforze i zaksięgowane, zapis ręczny, storno.</summary>
public sealed partial class LedgerViewModel : SectionViewModel
{
    public LedgerViewModel(AppSession s, IDialogService d) : base(s, d, "Księgowość", "📒") { }

    [ObservableProperty] private TableData _entries = TableData.Empty;
    [ObservableProperty] private TableRow? _selected;
    [ObservableProperty] private bool _showBuffer = true;
    [ObservableProperty] private bool _showPosted = true;
    [ObservableProperty] private List<string> _journalCodes = new();

    [ObservableProperty] private bool _editorVisible;
    [ObservableProperty] private string _journalCode = "PK";
    [ObservableProperty] private string _operationDate = "";
    [ObservableProperty] private string _documentDate = "";
    [ObservableProperty] private string _postingDate = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _documentType = "PK";
    [ObservableProperty] private string _documentNumber = "";
    private Guid? _editedDraftId;
    public PostingLinesEditor Lines { get; } = new();

    private Guid SelectedId => Selected?.Tag is Guid g ? g : throw new DomainException("Zaznacz zapis na liście.");

    partial void OnShowBufferChanged(bool value) => _ = LoadAsync();
    partial void OnShowPostedChanged(bool value) => _ = LoadAsync();

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var year = Session.CurrentYear;
        JournalCodes = await db.Journals.AsNoTracking().Where(j => j.CompanyId == Session.CompanyId && j.IsActive).OrderBy(j => j.Code).Select(j => j.Code).ToListAsync();
        var q = db.JournalEntries.AsNoTracking().Include(e => e.Lines).Where(e => e.FiscalYearId == year.Id);
        if (!ShowBuffer) q = q.Where(e => e.Status != EntryStatus.Bufor);
        if (!ShowPosted) q = q.Where(e => e.Status != EntryStatus.Zaksiegowany);
        var list = await q.ToListAsync();
        var t = new TableData(new TableColumn("Lp.", ColumnKind.Integer, 0.5), new TableColumn("Nr w dzienniku", Width: 1.4), new TableColumn("Dziennik", Width: 0.6),
            new TableColumn("Data księgowania", ColumnKind.Date, 0.9), new TableColumn("Dowód", Width: 1.3), new TableColumn("Opis", Width: 2.5),
            new TableColumn("Kwota", ColumnKind.Money), new TableColumn("Status", Width: 0.8), new TableColumn("Rodzaj", Width: 0.8), new TableColumn("Uwagi", Width: 1.2));
        foreach (var e in list.OrderBy(e => e.SequenceNumber ?? long.MaxValue).ThenBy(e => e.PostingDate))
            t.Add(e.Id, e.SequenceNumber, e.JournalNumber ?? "(bufor)", e.JournalCode, e.PostingDate, $"{e.DocumentType} {e.DocumentNumber}", e.Description,
                e.TotalDebit, e.Status, e.Kind, e.ReversedByEntryId is not null ? "wystornowany" : e.ReversesEntryId is not null ? "storno" : "");
        Entries = t;
    }

    [RelayCommand]
    private Task DetailsAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var e = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstAsync(x => x.Id == SelectedId);
        var cps = await db.Counterparties.AsNoTracking().Where(c => e.Lines.Select(l => l.CounterpartyId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code);
        var names = await db.Accounts.AsNoTracking().Where(a => e.Lines.Select(l => l.AccountId).Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name);
        var sb = new StringBuilder();
        sb.AppendLine($"{e.JournalNumber ?? "Zapis w buforze"}  (poz. dziennika {e.SequenceNumber})  status: {Display.Enum(e.Status)}, rodzaj: {Display.Enum(e.Kind)}");
        sb.AppendLine($"Dowód: {e.DocumentType} {e.DocumentNumber} z dnia {e.DocumentDate.ToPl()}; data operacji {e.OperationDate.ToPl()}; data księgowania {e.PostingDate.ToPl()}");
        sb.AppendLine($"Opis: {e.Description}");
        if (e.CorrectionReason is not null) sb.AppendLine($"Przyczyna korekty: {e.CorrectionReason}");
        if (e.PostedAtUtc is { } at) sb.AppendLine($"Zaksięgował: {e.PostedByUserId} dnia {at.ToPlDateTime()}");
        if (e.Hash is not null) sb.AppendLine($"Skrót SHA-256: {e.Hash}");
        sb.AppendLine();
        foreach (var l in e.Lines.OrderBy(l => l.LineNo))
            sb.AppendLine($"{l.LineNo,3}. {l.AccountNumber,-12} {names.GetValueOrDefault(l.AccountId, ""),-35} Wn {Money.Format(l.Debit),14}  Ma {Money.Format(l.Credit),14}  {(l.CounterpartyId is { } c ? cps.GetValueOrDefault(c) : "")} {l.Description}");
        sb.AppendLine($"Razem: Wn {Money.Format(e.TotalDebit)}  Ma {Money.Format(e.TotalCredit)}");
        await Dialogs.ShowTextAsync("Zapis księgowy", sb.ToString());
    });

    [RelayCommand]
    private Task NewEntryAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        Lines.Reset(await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        var today = Session.Clock.Today;
        var y = Session.CurrentYear;
        if (!y.Contains(today)) today = y.EndDate < today ? y.EndDate : y.StartDate;
        OperationDate = DocumentDate = PostingDate = today.ToPl();
        Description = DocumentNumber = "";
        DocumentType = "PK";
        JournalCode = JournalCodes.Contains("PK") ? "PK" : JournalCodes.FirstOrDefault() ?? "PK";
        _editedDraftId = null;
        EditorVisible = true;
    });

    [RelayCommand]
    private Task EditDraftAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var e = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstAsync(x => x.Id == SelectedId);
        if (e.Status != EntryStatus.Bufor) throw new DomainException("Edytować można wyłącznie zapis w buforze. Zapis zaksięgowany koryguje się stornem.");
        if (e.SourceDocumentId is not null) throw new DomainException("Zapis pochodzi z dokumentu — zmień dekretację dokumentu.");
        var cps = await CounterpartyChoice.LoadAsync(db, Session.CompanyId);
        Lines.Reset(cps);
        Lines.Lines.Clear();
        foreach (var l in e.Lines.OrderBy(l => l.LineNo))
            Lines.Lines.Add(new PostingLineRow
            {
                Account = l.AccountNumber, Debit = l.Debit == 0 ? "" : Money.Format(l.Debit), Credit = l.Credit == 0 ? "" : Money.Format(l.Credit),
                Counterparty = cps.FirstOrDefault(c => c.Id == l.CounterpartyId), Description = l.Description ?? ""
            });
        JournalCode = e.JournalCode;
        OperationDate = e.OperationDate.ToPl();
        DocumentDate = e.DocumentDate.ToPl();
        PostingDate = e.PostingDate.ToPl();
        Description = e.Description;
        DocumentType = e.DocumentType;
        DocumentNumber = e.DocumentNumber;
        _editedDraftId = e.Id;
        EditorVisible = true;
    });

    [RelayCommand] private void CloseEditor() => EditorVisible = false;

    private EntryInput Input() => new(JournalCode, Parse.Date(OperationDate, "Data operacji"), Parse.Date(DocumentDate, "Data dowodu"),
        Parse.Date(PostingDate, "Data księgowania"), Description.Trim(), DocumentType.Trim(), DocumentNumber.Trim(), Lines.ToInputs());

    [RelayCommand]
    private Task SaveDraftAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var svc = new LedgerService(db, Session.User, Session.Clock);
        if (_editedDraftId is { } id) await svc.UpdateDraftAsync(id, Input());
        else await svc.CreateDraftAsync(Input());
        EditorVisible = false;
    }, "Zapisano w buforze.");

    [RelayCommand]
    private Task SaveAndPostAsync() => ChangeAsync(async () =>
    {
        var input = Input();
        if (!await Dialogs.ConfirmAsync("Księgowanie", "Zaksięgować zapis? Zapis zaksięgowany jest trwały — późniejsza zmiana wyłącznie przez storno.")) return;
        await using var db = Session.OpenDb();
        var svc = new LedgerService(db, Session.User, Session.Clock);
        var entry = _editedDraftId is { } id ? await svc.PostAsync((await svc.UpdateDraftAsync(id, input)).Id) : await svc.CreateAndPostAsync(input);
        await db.SaveChangesAsync();
        EditorVisible = false;
        StatusMessage = $"Zaksięgowano: {entry.JournalNumber}";
    }, "Zapis zaksięgowany.");

    [RelayCommand]
    private Task PostDraftAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        if (!await Dialogs.ConfirmAsync("Księgowanie", "Zaksięgować zaznaczony zapis z bufora?")) return;
        await using var db = Session.OpenDb();
        await new LedgerService(db, Session.User, Session.Clock).PostAsync(id);
    }, "Zapis zaksięgowany.");

    [RelayCommand]
    private Task DeleteDraftAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        if (!await Dialogs.ConfirmAsync("Bufor", "Usunąć zaznaczony zapis z bufora?")) return;
        await using var db = Session.OpenDb();
        await new LedgerService(db, Session.User, Session.Clock).DeleteDraftAsync(id);
    }, "Usunięto zapis z bufora.");

    [RelayCommand]
    private Task ReverseAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        var reason = await Dialogs.PromptAsync("Storno", "Zapis zostanie wystornowany zgodnie z polityką rachunkowości (storno czerwone lub czarne). Podaj przyczynę korekty:");
        if (string.IsNullOrWhiteSpace(reason)) return;
        var date = await Dialogs.PromptAsync("Storno", "Data księgowania storna (dd.mm.rrrr):", Session.Clock.Today.ToPl());
        if (date is null) return;
        await using var db = Session.OpenDb();
        var r = await new LedgerService(db, Session.User, Session.Clock).ReverseAsync(id, reason.Trim(), Parse.Date(date, "Data storna"));
        StatusMessage = $"Utworzono storno {r.JournalNumber}";
    }, "Zapis wystornowany.");
}
