using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Infrastructure.Ksef;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Wspólna lista dokumentów z operacjami: szczegóły, zatwierdzenie, dekretacja, księgowanie, anulowanie, załączniki.</summary>
public abstract partial class DocumentListBase : SectionViewModel
{
    protected DocumentListBase(AppSession s, IDialogService d, string title, string icon) : base(s, d, title, icon) { }

    [ObservableProperty] private TableData _documents = TableData.Empty;
    [ObservableProperty] private TableRow? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyCurrentYear = true;

    protected abstract IQueryable<Document> Filter(IQueryable<Document> q);

    protected Guid SelectedId => Selected?.Tag is Guid g ? g : throw new DomainException("Zaznacz dokument na liście.");

    /// <summary>Konta planu kont roku obejmującego datę (do doboru konta według kategorii wydatku).</summary>
    protected async Task<List<Domain.Accounts.Account>> AccountsForAsync(PelnaKsiegowosc.Data.KsiegiDbContext db, DateOnly date)
    {
        var fy = Session.Years.FirstOrDefault(y => y.Contains(date)) ?? Session.CurrentYear;
        var chart = await db.Charts.AsNoTracking().Where(c => c.FiscalYearId == fy.Id).OrderByDescending(c => c.Version).FirstOrDefaultAsync();
        return chart is null ? new() : await db.Accounts.AsNoTracking().Where(a => a.ChartId == chart.Id).ToListAsync();
    }

    /// <summary>Zatwierdzenie i zaksięgowanie zapisanego dokumentu jednym krokiem (dla prostego trybu pracy).</summary>
    protected async Task<string> ApproveAndPostAsync(Guid id)
    {
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        var d = await svc.GetAsync(id);
        var v = await svc.ValidateAsync(d);
        if (!v.IsValid) throw new DomainException("Dokument zapisano jako roboczy, ale nie można go zaksięgować:" + Environment.NewLine + string.Join(Environment.NewLine, v.Errors));
        await svc.ApproveAsync(id);
        var e = await svc.PostAsync(id);
        return e.JournalNumber ?? "";
    }

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var q = Filter(db.Documents.AsNoTracking().Where(d => d.CompanyId == Session.CompanyId));
        if (OnlyCurrentYear && Session.Year is { } y) q = q.Where(d => d.IssueDate >= y.StartDate && d.IssueDate <= y.EndDate);
        var list = await q.OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.CreatedAtUtc).Take(5000).ToListAsync();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            list = list.Where(d => d.Number.Contains(s, StringComparison.OrdinalIgnoreCase) || (d.ExternalNumber ?? "").Contains(s, StringComparison.OrdinalIgnoreCase)
                                   || (d.CounterpartyName ?? "").Contains(s, StringComparison.OrdinalIgnoreCase) || d.Description.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var t = new TableData(new TableColumn("Numer", Width: 1.4), new TableColumn("Rodzaj", Width: 1.4), new TableColumn("Data", ColumnKind.Date, 0.8),
            new TableColumn("Kontrahent", Width: 2), new TableColumn("Nr obcy", Width: 1.1), new TableColumn("Netto", ColumnKind.Money), new TableColumn("VAT", ColumnKind.Money),
            new TableColumn("Brutto", ColumnKind.Money), new TableColumn("Waluta", Width: 0.5), new TableColumn("Status", Width: 0.9), new TableColumn("Płatność", Width: 0.9),
            new TableColumn("KSeF", Width: 0.9));
        foreach (var d in list)
            t.Add(d.Id, d.Number, Document.KindName(d.Kind), d.IssueDate, d.CounterpartyName, d.ExternalNumber, d.NetAmount, d.VatAmount, d.GrossAmount,
                d.Currency, d.Status, d.PaymentStatus, d.KsefNumber ?? (d.KsefStatus == KsefStatus.NieDotyczy ? "" : Display.Enum(d.KsefStatus)));
        Documents = t;
        Selected = null;
    }

    partial void OnOnlyCurrentYearChanged(bool value) => _ = LoadAsync();

    [RelayCommand] private Task SearchNowAsync() => LoadAsync();

    [RelayCommand]
    private Task DetailsAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        var d = await svc.GetAsync(SelectedId);
        var accounts = await db.Accounts.AsNoTracking().Where(a => d.PostingLines.Select(p => p.AccountId).Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Number);
        var history = await db.DocumentHistory.AsNoTracking().Where(h => h.DocumentId == d.Id).OrderBy(h => h.AtUtc).ToListAsync();
        var attachments = await db.Attachments.AsNoTracking().Where(a => a.DocumentId == d.Id).ToListAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"{Document.KindName(d.Kind)} {d.Number}   status: {Display.Enum(d.Status)}");
        if (d.ExternalNumber is not null) sb.AppendLine($"Numer obcy: {d.ExternalNumber}");
        sb.AppendLine($"Kontrahent: {d.CounterpartyName} {(d.CounterpartyNip is null ? "" : "NIP " + d.CounterpartyNip)}; {d.CounterpartyAddress}");
        sb.AppendLine($"Data wystawienia: {d.IssueDate.ToPl()}; operacji: {d.OperationDate.ToPl()}; wpływu: {d.ReceiptDate.ToPl()}; termin płatności: {d.DueDate.ToPl()}");
        sb.AppendLine($"Opis: {d.Description}");
        if (d.Currency != "PLN") sb.AppendLine($"Waluta {d.Currency}, kurs {d.ExchangeRate} (tabela {d.ExchangeRateTable}, z dnia {d.ExchangeRateDate.ToPl()})");
        if (d.CorrectionReason is not null) sb.AppendLine($"Przyczyna korekty: {d.CorrectionReason}");
        if (d.KsefNumber is not null) sb.AppendLine($"Numer KSeF: {d.KsefNumber}");
        sb.AppendLine();
        if (d.Lines.Count > 0)
        {
            sb.AppendLine("Pozycje:");
            foreach (var l in d.Lines.OrderBy(l => l.LineNo))
                sb.AppendLine($"  {l.LineNo}. {l.Name} — {l.Quantity:0.####} {l.Unit} × {Money.Format(l.UnitNetPrice)} = netto {Money.Format(l.NetAmount)}, VAT {l.VatRateCode}: {Money.Format(l.VatAmount)}");
            sb.AppendLine("Podsumowanie VAT:");
            foreach (var v in d.VatLines)
                sb.AppendLine($"  {v.VatRateCode}: netto {Money.Format(v.NetAmountPln)} PLN, VAT {Money.Format(v.VatAmountPln)} PLN" + (d.IsPurchase ? $", odliczenie {v.DeductionPercent:0.##}% = {Money.Format(v.DeductibleVatPln)}{(v.IsFixedAsset ? " (środki trwałe)" : "")}" : ""));
            sb.AppendLine($"Razem: netto {Money.Format(d.NetAmount)}, VAT {Money.Format(d.VatAmount)}, brutto {Money.Format(d.GrossAmount)} {d.Currency}");
            sb.AppendLine();
        }
        if (d.PostingLines.Count > 0)
        {
            sb.AppendLine("Dekretacja:");
            foreach (var p in d.PostingLines.OrderBy(p => p.LineNo))
                sb.AppendLine($"  {accounts.GetValueOrDefault(p.AccountId, "?"),-12} Wn {Money.Format(p.Debit),14}  Ma {Money.Format(p.Credit),14}  {p.Description}");
            sb.AppendLine();
        }
        if (attachments.Count > 0)
        {
            sb.AppendLine("Załączniki:");
            foreach (var a in attachments) sb.AppendLine($"  {a.FileName} ({a.SizeBytes:N0} B, SHA-256 {a.Sha256[..16]}…)");
            sb.AppendLine();
        }
        sb.AppendLine("Historia:");
        foreach (var h in history) sb.AppendLine($"  {h.AtUtc.ToPlDateTime()}  {h.Action}  {h.Details}");
        await Dialogs.ShowTextAsync($"Dokument {d.Number}", sb.ToString());
    });

    [RelayCommand]
    private async Task InstallmentsAsync()
    {
        Guid id;
        try { id = SelectedId; } catch (DomainException ex) { await Dialogs.ErrorAsync("Raty", ex.Message); return; }
        string current = "";
        await RunAsync(async () =>
        {
            await using var db = Session.OpenDb();
            current = string.Join("; ", (await new DocumentService(db, Session.User, Session.Clock).InstallmentsAsync(id)).Select(i => $"{i.DueDate.ToPl()} {Money.Format(i.Amount)}"));
        });
        var text = await Dialogs.PromptAsync("Płatność w ratach",
            "Podaj raty jako „termin kwota” oddzielone średnikami, np.: 31.01.2026 1000,00; 28.02.2026 1000,00; 31.03.2026 460,00. " +
            "Suma rat = kwota brutto. Puste pole usuwa harmonogram.", current);
        if (text is null) return;
        await ChangeAsync(async () =>
        {
            var list = new List<(DateOnly, decimal)>();
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bits = part.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (bits.Length != 2) throw new DomainException($"Nieprawidłowa rata „{part}” — oczekiwano „termin kwota”.");
                list.Add((Parse.Date(bits[0], "Termin raty"), Parse.Amount(bits[1], "Kwota raty")));
            }
            await using var db = Session.OpenDb();
            await new DocumentService(db, Session.User, Session.Clock).SetInstallmentsAsync(id, list);
        }, "Zapisano harmonogram rat.");
    }

    [RelayCommand]
    private Task ApproveAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new DocumentService(db, Session.User, Session.Clock).ApproveAsync(SelectedId);
    }, "Dokument zatwierdzony.");

    [RelayCommand]
    private Task UnapproveAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        var reason = await Dialogs.PromptAsync("Cofnięcie zatwierdzenia", "Podaj przyczynę cofnięcia zatwierdzenia:");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await using var db = Session.OpenDb();
        await new DocumentService(db, Session.User, Session.Clock).UnapproveAsync(id, reason.Trim());
    }, "Cofnięto zatwierdzenie.");

    [RelayCommand]
    private Task ProposePostingAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        var lines = await svc.ProposePostingAsync(SelectedId);
        var accounts = await db.Accounts.AsNoTracking().Where(a => lines.Select(l => l.AccountId).Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => $"{a.Number} {a.Name}");
        await Dialogs.ShowTextAsync("Proponowana dekretacja", string.Join(Environment.NewLine,
            lines.Select(l => $"{accounts.GetValueOrDefault(l.AccountId, "?"),-45} Wn {Money.Format(l.Debit),14}  Ma {Money.Format(l.Credit),14}  {l.Description}")));
    }, "Zapisano propozycję dekretacji.");

    [RelayCommand]
    private Task PostAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        if (!await Dialogs.ConfirmAsync("Księgowanie", "Zaksięgować dokument? Zapis w dzienniku jest trwały — późniejsza zmiana wymaga korekty (storna).")) return;
        await using var db = Session.OpenDb();
        var entry = await new DocumentService(db, Session.User, Session.Clock).PostAsync(id);
        StatusMessage = $"Zaksięgowano: {entry.JournalNumber}";
    }, "Dokument zaksięgowany.");

    [RelayCommand]
    private Task CancelDocumentAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        var reason = await Dialogs.PromptAsync("Anulowanie dokumentu", "Dokument niezaksięgowany zostanie anulowany (numer pozostaje zajęty). Podaj przyczynę:");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await using var db = Session.OpenDb();
        await new DocumentService(db, Session.User, Session.Clock).CancelAsync(id, reason.Trim());
    }, "Dokument anulowany.");

    [RelayCommand]
    private Task AddAttachmentAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        var file = await Dialogs.OpenFileAsync("Załącznik do dokumentu", new FileFilter("Dokumenty", "*.pdf", "*.xml", "*.jpg", "*.jpeg", "*.png", "*.tif", "*.tiff"), new FileFilter("Wszystkie pliki", "*"));
        if (file is null) return;
        await using var db = Session.OpenDb();
        await using var fs = File.OpenRead(file);
        await new DocumentService(db, Session.User, Session.Clock).AddAttachmentAsync(id, Session.Attachments, Path.GetFileName(file), fs);
    }, "Dodano załącznik.");

    [RelayCommand]
    private Task SaveAttachmentAsync() => RunAsync(async () =>
    {
        var id = SelectedId;
        await using var db = Session.OpenDb();
        var list = await db.Attachments.AsNoTracking().Where(a => a.DocumentId == id).OrderBy(a => a.CreatedAtUtc).ToListAsync();
        if (list.Count == 0) throw new DomainException("Dokument nie ma załączników.");
        var a = list[0];
        if (list.Count > 1)
        {
            var pick = await Dialogs.PromptAsync("Załączniki", "Podaj numer załącznika do zapisania:" + Environment.NewLine +
                string.Join(Environment.NewLine, list.Select((x, i) => $"{i + 1}. {x.FileName}")), "1");
            if (pick is null) return;
            a = list[Math.Clamp(Parse.Int(pick, "Numer załącznika") - 1, 0, list.Count - 1)];
        }
        var target = await Dialogs.SaveFileAsync("Zapisz załącznik", a.FileName);
        if (target is null) return;
        var bytes = await new DocumentService(db, Session.User, Session.Clock).ReadAttachmentAsync(a.Id, Session.Attachments);
        await File.WriteAllBytesAsync(target, bytes);
        StatusMessage = $"Zapisano {target} (suma SHA-256 zgodna).";
    });
}

/// <summary>Wszystkie dokumenty księgowe oraz dokumenty z ręczną dekretacją (PK, NK, DW, dowody zastępcze).</summary>
public sealed partial class DocumentsViewModel : DocumentListBase
{
    public DocumentsViewModel(AppSession s, IDialogService d) : base(s, d, "Dokumenty", "📄")
    {
        GeneralKind = GeneralKinds[0];
    }

    public List<Choice<DocumentKind?>> KindFilters { get; } =
        new[] { new Choice<DocumentKind?>(null, "(wszystkie rodzaje)") }.Concat(Enum.GetValues<DocumentKind>().Select(k => new Choice<DocumentKind?>(k, Document.KindName(k)))).ToList();
    public List<Choice<DocumentStatus?>> StatusFilters { get; } =
        new[] { new Choice<DocumentStatus?>(null, "(wszystkie statusy)") }.Concat(Enum.GetValues<DocumentStatus>().Select(k => new Choice<DocumentStatus?>(k, Display.Enum(k)))).ToList();

    [ObservableProperty] private Choice<DocumentKind?>? _kindFilter;
    [ObservableProperty] private Choice<DocumentStatus?>? _statusFilter;

    public List<Choice<DocumentKind>> GeneralKinds { get; } = new[]
    {
        DocumentKind.PolecenieKsiegowania, DocumentKind.NotaKsiegowa, DocumentKind.DokumentWewnetrzny, DocumentKind.DowodZastepczy, DocumentKind.RozliczenieZAkcjonariuszem, DocumentKind.Inny
    }.Select(k => new Choice<DocumentKind>(k, Document.KindName(k))).ToList();

    [ObservableProperty] private bool _editorVisible;
    [ObservableProperty] private Choice<DocumentKind> _generalKind;
    [ObservableProperty] private string _generalDate = "";
    [ObservableProperty] private string _generalDescription = "";
    [ObservableProperty] private string _generalExternalNumber = "";
    [ObservableProperty] private CounterpartyChoice? _generalCounterparty;
    public PostingLinesEditor Lines { get; } = new();

    partial void OnKindFilterChanged(Choice<DocumentKind?>? value) => _ = LoadAsync();
    partial void OnStatusFilterChanged(Choice<DocumentStatus?>? value) => _ = LoadAsync();

    protected override IQueryable<Document> Filter(IQueryable<Document> q)
    {
        if (KindFilter?.Value is { } k) q = q.Where(d => d.Kind == k);
        if (StatusFilter?.Value is { } s) q = q.Where(d => d.Status == s);
        return q;
    }

    [RelayCommand]
    private Task NewGeneralAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        Lines.Reset(await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        GeneralDate = Session.Clock.Today.ToPl();
        GeneralDescription = GeneralExternalNumber = "";
        GeneralCounterparty = null;
        EditorVisible = true;
    });

    [RelayCommand] private void CloseEditor() => EditorVisible = false;

    [RelayCommand]
    private Task SaveGeneralAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var doc = await new DocumentService(db, Session.User, Session.Clock).CreateGeneralDocumentAsync(GeneralKind.Value, Parse.Date(GeneralDate, "Data"),
            GeneralDescription, Lines.ToInputs(), GeneralCounterparty?.Id, Parse.Trim(GeneralExternalNumber));
        EditorVisible = false;
        StatusMessage = $"Utworzono {doc.Number}";
    }, "Utworzono dokument (roboczy) — zatwierdź go i zaksięguj.");
}

/// <summary>Faktury sprzedaży: wystawianie, korekty, KSeF, plik FA(3).</summary>
public sealed partial class SalesViewModel : DocumentListBase
{
    public SalesViewModel(AppSession s, IDialogService d) : base(s, d, "Sprzedaż", "↗") { }

    public InvoiceEditor Editor { get; } = new(purchase: false);
    [ObservableProperty] private bool _editorVisible;
    [ObservableProperty] private bool _correctionMode;
    [ObservableProperty] private string _correctionReason = "";
    private Guid? _correctedId;

    protected override IQueryable<Document> Filter(IQueryable<Document> q) =>
        q.Where(d => d.Kind == DocumentKind.FakturaSprzedazy || d.Kind == DocumentKind.KorektaSprzedazy);

    [RelayCommand]
    private Task NewAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        Editor.Accounts = await AccountsForAsync(db, Session.Clock.Today);
        Editor.Reset(Session.Clock.Today, await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        CorrectionMode = false;
        EditorVisible = true;
    });

    [RelayCommand]
    private Task EditAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var d = await new DocumentService(db, Session.User, Session.Clock).GetAsync(SelectedId);
        if (d.Status != DocumentStatus.Roboczy) throw new DomainException("Edytować można tylko dokument roboczy. Dokument zaksięgowany zmienia się fakturą korygującą.");
        if (d.IsCorrection) throw new DomainException("Korekty robocze anuluj i wystaw ponownie.");
        Editor.Load(d, await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        CorrectionMode = false;
        EditorVisible = true;
    });

    [RelayCommand]
    private Task NewCorrectionAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var d = await new DocumentService(db, Session.User, Session.Clock).GetAsync(SelectedId);
        if (d.Status != DocumentStatus.Zaksiegowany || d.IsCorrection) throw new DomainException("Korektę wystawia się do zaksięgowanej faktury pierwotnej.");
        Editor.Load(d, await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        Editor.Header = $"Korekta do {d.Number} — wpisz RÓŻNICE (np. ilość −1 przy zwrocie, cena ujemna przy rabacie)";
        Editor.IssueDate = Session.Clock.Today.ToPl();
        foreach (var l in Editor.Lines) { l.Quantity = "0"; }
        _correctedId = d.Id;
        CorrectionReason = "";
        CorrectionMode = true;
        EditorVisible = true;
    });

    [RelayCommand] private void CloseEditor() => EditorVisible = false;

    private async Task<Document> SaveSaleAsync()
    {
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        Document doc;
        if (CorrectionMode)
        {
            var lines = Editor.LineInputs().Where(l => l.Quantity != 0 && l.UnitNetPrice != 0).ToList();
            doc = await svc.CreateCorrectionAsync(_correctedId!.Value, CorrectionReason, Parse.Date(Editor.IssueDate, "Data wystawienia"), lines);
        }
        else if (Editor.DocumentId is { } id) doc = await svc.UpdateInvoiceAsync(id, Editor.ToInput());
        else doc = await svc.CreateInvoiceAsync(Editor.ToInput());
        EditorVisible = false;
        StatusMessage = $"Zapisano {doc.Number}: brutto {Money.Format(doc.GrossAmount)} {doc.Currency}";
        return doc;
    }

    [RelayCommand]
    private Task SaveAsync() => ChangeAsync(async () => await SaveSaleAsync(), "Zapisano dokument roboczy.");

    [RelayCommand]
    private Task SaveAndPostAsync() => ChangeAsync(async () =>
    {
        var doc = await SaveSaleAsync();
        var entry = await ApproveAndPostAsync(doc.Id);
        var ksef = Session.Config.Ksef.Enabled && doc.KsefStatus == KsefStatus.DoWyslania
            ? " Wyślij fakturę do KSeF przyciskiem „Wyślij do KSeF”." : "";
        StatusMessage = $"Wystawiono i zaksięgowano {doc.Number} ({entry}).{ksef}";
        if (ksef.Length > 0) await Dialogs.InfoAsync("KSeF", $"Faktura {doc.Number} jest zaksięgowana.{ksef}");
    }, "Faktura zapisana i zaksięgowana.");

    [RelayCommand]
    private Task PrintPdfAsync() => RunAsync(async () =>
    {
        var id = SelectedId;
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        var d = await svc.GetAsync(id);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == d.CompanyId);
        var corrected = d.CorrectedDocumentId is { } cid ? await svc.GetAsync(cid) : null;
        var target = await Dialogs.SaveFileAsync("Zapisz fakturę (PDF)", $"{d.Number.Replace('/', '-')}.pdf", new FileFilter("PDF", "*.pdf"));
        if (target is null) return;
        await File.WriteAllBytesAsync(target, Infrastructure.Export.InvoicePdf.Build(d, company, corrected, await svc.InstallmentsAsync(id)));
        StatusMessage = $"Zapisano {target}";
        Dialogs.OpenInSystem(target);
    });

    [RelayCommand]
    private Task SaveXmlAsync() => RunAsync(async () =>
    {
        var id = SelectedId;
        await using var db = Session.OpenDb();
        var ksef = new KsefService(db, Session.User, Session.Clock, Session.Validator);
        var (xml, validation) = await ksef.BuildAsync(id);
        var target = await Dialogs.SaveFileAsync("Zapisz fakturę FA(3)", $"{Selected!.Cells[0].Replace('/', '-')}.xml", new FileFilter("XML", "*.xml"));
        if (target is null) return;
        await File.WriteAllBytesAsync(target, xml);
        await Dialogs.InfoAsync("Plik FA(3)", validation.IsValid
            ? "Plik zapisano i zwalidowano ze schematem XSD FA(3)."
            : "Plik zapisano, ale NIE przeszedł walidacji XSD:" + Environment.NewLine + string.Join(Environment.NewLine, validation.Errors.Take(20)));
    });

    [RelayCommand]
    private Task SendToKsefAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        if (!Session.Config.Ksef.Enabled) throw new DomainException("Integracja z KSeF jest wyłączona — skonfiguruj ją w sekcji „Integracje”.");
        var credentials = Session.KsefCredentials();
        if (!await Dialogs.ConfirmAsync("KSeF", $"Wysłać fakturę do KSeF (środowisko: {Session.Config.Ksef.Environment})?")) return;
        await using var db = Session.OpenDb();
        using var http = new HttpClient { BaseAddress = KsefClient.BaseUrl(Session.Config.Ksef.Environment), Timeout = TimeSpan.FromSeconds(120) };
        var ksef = new KsefService(db, Session.User, Session.Clock, Session.Validator);
        var r = await ksef.SendAsync(id, new KsefClient(http), credentials);
        if (r.Accepted) { Session.Config.Ksef.LastSuccessUtc = Session.Clock.UtcNow; Session.SaveConfig(); }
        await Dialogs.InfoAsync("KSeF", r.Message + (r.KsefNumber is null ? "" : $"{Environment.NewLine}Numer KSeF: {r.KsefNumber}"));
    }, "Zakończono operację KSeF.");
}

/// <summary>Faktury zakupu i rachunki: rejestracja, odliczenie VAT, import z KSeF.</summary>
public sealed partial class PurchasesViewModel : DocumentListBase
{
    public PurchasesViewModel(AppSession s, IDialogService d) : base(s, d, "Zakupy", "↙") { }

    public InvoiceEditor Editor { get; } = new(purchase: true);
    [ObservableProperty] private bool _editorVisible;
    [ObservableProperty] private bool _correctionMode;
    [ObservableProperty] private string _correctionReason = "";
    private Guid? _correctedId;

    [ObservableProperty] private string _deductionPercent = "100";
    [ObservableProperty] private bool _deductionFixedAsset;
    [ObservableProperty] private string _deductionPeriod = "";

    protected override IQueryable<Document> Filter(IQueryable<Document> q) =>
        q.Where(d => d.Kind == DocumentKind.FakturaZakupu || d.Kind == DocumentKind.KorektaZakupu || d.Kind == DocumentKind.Rachunek);

    [RelayCommand]
    private Task NewAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        Editor.Accounts = await AccountsForAsync(db, Session.Clock.Today);
        Editor.Reset(Session.Clock.Today, await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        CorrectionMode = false;
        EditorVisible = true;
    });

    [RelayCommand]
    private Task EditAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var d = await new DocumentService(db, Session.User, Session.Clock).GetAsync(SelectedId);
        Editor.Accounts = await AccountsForAsync(db, d.IssueDate);
        if (d.Status != DocumentStatus.Roboczy) throw new DomainException("Edytować można tylko dokument roboczy.");
        if (d.IsCorrection) throw new DomainException("Korekty robocze anuluj i zarejestruj ponownie.");
        Editor.Load(d, await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        CorrectionMode = false;
        EditorVisible = true;
    });

    [RelayCommand]
    private Task NewCorrectionAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var d = await new DocumentService(db, Session.User, Session.Clock).GetAsync(SelectedId);
        if (d.Status != DocumentStatus.Zaksiegowany || d.IsCorrection || !d.IsInvoice) throw new DomainException("Korektę rejestruje się do zaksięgowanej faktury pierwotnej.");
        Editor.Accounts = await AccountsForAsync(db, Session.Clock.Today);
        Editor.Load(d, await CounterpartyChoice.LoadAsync(db, Session.CompanyId));
        Editor.Header = $"Korekta do {d.Number} ({d.ExternalNumber}) — wpisz RÓŻNICE pozycji";
        Editor.IssueDate = Session.Clock.Today.ToPl();
        foreach (var l in Editor.Lines) l.Quantity = "0";
        _correctedId = d.Id;
        CorrectionReason = "";
        CorrectionMode = true;
        EditorVisible = true;
    });

    [RelayCommand] private void CloseEditor() => EditorVisible = false;

    private async Task<Document> SavePurchaseAsync()
    {
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        Document doc;
        if (CorrectionMode)
            doc = await svc.CreateCorrectionAsync(_correctedId!.Value, CorrectionReason, Parse.Date(Editor.IssueDate, "Data wystawienia"),
                Editor.LineInputs().Where(l => l.Quantity != 0 && l.UnitNetPrice != 0).ToList());
        else if (Editor.DocumentId is { } id) doc = await svc.UpdateInvoiceAsync(id, Editor.ToInput());
        else doc = await svc.CreateInvoiceAsync(Editor.ToInput());
        var auto = await svc.ApplyCategoryDeductionAsync(doc.Id);
        EditorVisible = false;
        StatusMessage = $"Zarejestrowano {doc.Number}." + (auto ? " Odliczenie VAT ustalono według kategorii." : doc.VatAmount != 0 ? " Potwierdź odliczenie VAT." : "");
        return doc;
    }

    [RelayCommand]
    private Task SaveAsync() => ChangeAsync(async () => await SavePurchaseAsync(), "Zapisano dokument roboczy.");

    [RelayCommand]
    private Task SaveAndPostAsync() => ChangeAsync(async () =>
    {
        var doc = await SavePurchaseAsync();
        var entry = await ApproveAndPostAsync(doc.Id);
        StatusMessage = $"Zaksięgowano {doc.Number} ({entry}).";
    }, "Dokument zapisany i zaksięgowany.");

    [RelayCommand]
    private Task ConfirmDeductionAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var svc = new DocumentService(db, Session.User, Session.Clock);
        var d = await svc.GetAsync(SelectedId);
        var percent = Parse.Amount(DeductionPercent, "Procent odliczenia");
        var period = string.IsNullOrWhiteSpace(DeductionPeriod) ? DocumentService.DefaultVatPeriod(d) : Parse.Month(DeductionPeriod, "Okres odliczenia");
        var perRate = d.VatLines.ToDictionary(v => v.VatRateCode, _ => (percent, DeductionFixedAsset));
        await svc.SetVatDeductionAsync(d.Id, perRate, period);
    }, "Potwierdzono zakres i okres odliczenia VAT.");

    [ObservableProperty] private string _ksefFrom = "";
    [ObservableProperty] private string _ksefTo = "";

    [RelayCommand]
    private Task ImportFromKsefAsync() => ChangeAsync(async () =>
    {
        if (!Session.Config.Ksef.Enabled) throw new DomainException("Integracja z KSeF jest wyłączona — skonfiguruj ją w sekcji „Integracje”.");
        var credentials = Session.KsefCredentials();
        var from = Parse.OptionalDate(KsefFrom, "Od") ?? Session.Clock.Today.AddDays(-30);
        var to = Parse.OptionalDate(KsefTo, "Do") ?? Session.Clock.Today;
        await using var db = Session.OpenDb();
        using var http = new HttpClient { BaseAddress = KsefClient.BaseUrl(Session.Config.Ksef.Environment), Timeout = TimeSpan.FromSeconds(120) };
        var r = await new KsefService(db, Session.User, Session.Clock, Session.Validator).ImportPurchasesAsync(new KsefClient(http), credentials,
            new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero), Session.Attachments);
        await Dialogs.InfoAsync("Import z KSeF", $"Znaleziono: {r.Found}, zaimportowano: {r.Imported}, już w ewidencji: {r.AlreadyPresent}." +
            (r.Errors.Count > 0 ? Environment.NewLine + "Błędy:" + Environment.NewLine + string.Join(Environment.NewLine, r.Errors) : ""));
    }, "Zakończono import z KSeF.");
}
