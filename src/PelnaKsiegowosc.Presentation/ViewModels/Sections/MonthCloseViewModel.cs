using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Application.Periods;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Krok kreatora zamknięcia miesiąca: stan, objaśnienie i czynność wykonywana jednym przyciskiem.</summary>
public sealed partial class CloseStep : ObservableObject
{
    public CloseStep(int number, string title, string explanation, string actionLabel, Func<Task> action)
    {
        Number = number;
        Title = title;
        Explanation = explanation;
        ActionLabel = actionLabel;
        ActionCommand = new AsyncRelayCommand(action);
    }

    public int Number { get; }
    public string Title { get; }
    public string Explanation { get; }
    public string ActionLabel { get; }
    public IAsyncRelayCommand ActionCommand { get; }
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _applicable = true;
    [ObservableProperty] private string _status = "";

    public string Mark => !Applicable ? "–" : Done ? "✔" : "○";
    public string Color => !Applicable ? "#90A4AE" : Done ? "#2E7D32" : "#E08A00";
    partial void OnDoneChanged(bool value) { OnPropertyChanged(nameof(Mark)); OnPropertyChanged(nameof(Color)); }
    partial void OnApplicableChanged(bool value) { OnPropertyChanged(nameof(Mark)); OnPropertyChanged(nameof(Color)); }
}

/// <summary>
/// Kreator zamknięcia miesiąca krok po kroku: dokumenty, bank i kasa, amortyzacja, VAT i JPK, zaliczka CIT, kopia,
/// zamknięcie okresu. Każdy krok pokazuje stan i ma przycisk wykonujący czynność.
/// </summary>
public sealed partial class MonthCloseViewModel : ViewModelBase
{
    private readonly AppSession _session;

    public MonthCloseViewModel(AppSession session, IDialogService dialogs) : base(dialogs)
    {
        _session = session;
        var prev = new DateOnly(session.Clock.Today.Year, session.Clock.Today.Month, 1).AddMonths(-1);
        _month = $"{prev.Month:00}.{prev.Year}";
        Steps = new List<CloseStep>
        {
            new(1, "Dokumenty miesiąca zatwierdzone i zaksięgowane",
                "Każdą fakturę i dowód z tego miesiąca trzeba zaksięgować — księgi prowadzi się bieżąco (przyjęty termin: 15. dzień następnego miesiąca). Przycisk zatwierdza i księguje wszystkie poprawne dokumenty; błędne zostaną wymienione.",
                "Zaksięguj wszystkie poprawne", PostDocumentsAsync),
            new(2, "Bank i kasa rozliczone",
                "Wyciągi z rachunków za cały miesiąc powinny być zaimportowane, a każda operacja dopasowana do faktury lub konta i zaksięgowana.",
                "Zaksięguj dopasowane operacje", PostBankAsync),
            new(3, "Amortyzacja środków trwałych", "Odpisy amortyzacyjne za miesiąc (jeżeli firma ma środki trwałe).", "Nalicz amortyzację", DepreciationAsync),
            new(4, "Rozliczenie VAT zaksięgowane", "Kontrola rejestrów VAT i przeksięgowanie VAT należnego i naliczonego na rozrachunek z urzędem skarbowym.",
                "Sprawdź i zaksięguj VAT", VatAsync),
            new(5, "Plik JPK_V7M lub JPK_V7K przygotowany", "Plik do wysłania do urzędu skarbowego do 25. dnia następnego miesiąca (kwartalnie: co miesiąc ewidencja, deklaracja po kwartale).", "Generuj plik JPK", JpkAsync),
            new(6, "Zaliczka CIT wyliczona i zatwierdzona", "Zaliczkę na podatek dochodowy wpłaca się do 20. dnia następnego miesiąca.", "Wylicz i zatwierdź zaliczkę", CitAsync),
            new(7, "Kopia zapasowa", "Kopia po zaksięgowaniu miesiąca chroni wykonaną pracę.", "Wykonaj kopię", BackupAsync),
            new(8, "Miesiąc zamknięty", "Zamknięcie blokuje przypadkowe zmiany w tym miesiącu (można go później otworzyć z uzasadnieniem).",
                "Zamknij miesiąc", ClosePeriodAsync),
        };
    }

    public List<CloseStep> Steps { get; }
    [ObservableProperty] private string _month;
    [ObservableProperty] private string _summary = "";

    private DateOnly MonthStart => Parse.Month(Month, "Miesiąc");
    private DateOnly MonthEnd => MonthStart.AddMonths(1).AddDays(-1);

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        if (!_session.Years.Any(y => y.Contains(MonthStart)) && _session.Year is { } cy)
        {
            var m = _session.Clock.Today < cy.StartDate || _session.Clock.Today > cy.EndDate ? cy.StartDate : new DateOnly(_session.Clock.Today.Year, _session.Clock.Today.Month, 1);
            Month = $"{m.Month:00}.{m.Year}";
        }
        var from = MonthStart;
        var to = MonthEnd;
        await using var db = _session.OpenDb();
        var companyId = _session.CompanyId;
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId);
        var fy = _session.Years.FirstOrDefault(y => y.Contains(from)) ?? throw new DomainException("Miesiąc poza latami obrotowymi.");
        var period = fy.Periods.First(p => p.Contains(from));

        var docs = await db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && d.Status != DocumentStatus.Anulowany && d.IssueDate >= from && d.IssueDate <= to).ToListAsync();
        var open = docs.Count(d => d.Status != DocumentStatus.Zaksiegowany);
        Steps[0].Done = open == 0;
        Steps[0].Status = open == 0 ? $"Wszystkie dokumenty ({docs.Count}) zaksięgowane." : $"Do zaksięgowania: {open} z {docs.Count}.";

        var tx = await db.MoneyTransactions.AsNoTracking().Where(t => t.CompanyId == companyId && t.BookingDate >= from && t.BookingDate <= to).ToListAsync();
        var openTx = tx.Count(t => t.Status is TransactionStatus.Nierozliczona or TransactionStatus.Dopasowana);
        var bankAccounts = await db.MoneyAccounts.AsNoTracking().Where(m => m.CompanyId == companyId && m.IsActive && m.Kind == MoneyAccountKind.RachunekBankowy).ToListAsync();
        var missing = new List<string>();
        foreach (var m in bankAccounts)
            if (!await db.MoneyStatements.AnyAsync(s => s.MoneyAccountId == m.Id && s.DateTo >= to)) missing.Add(m.Name);
        Steps[1].Done = openTx == 0 && missing.Count == 0;
        Steps[1].Status = (missing.Count > 0 ? $"Brak wyciągu do końca miesiąca: {string.Join(", ", missing)}. " : "") +
                          (openTx > 0 ? $"Nierozliczone operacje: {openTx}." : $"Operacje rozliczone ({tx.Count}).");

        var assets = await db.FixedAssets.AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.Status == FixedAssetStatus.WEwidencji && a.BookMethod != DepreciationMethod.Brak && a.PlacedInServiceDate < from);
        var run = await db.DepreciationRuns.AsNoTracking().AnyAsync(r => r.CompanyId == companyId && r.Year == from.Year && r.Month == from.Month && !r.IsReversed);
        Steps[2].Applicable = assets;
        Steps[2].Done = !assets || run;
        Steps[2].Status = !assets ? "Brak środków trwałych do amortyzacji." : run ? "Amortyzacja naliczona." : "Amortyzacja nienaliczona.";

        var vatPayer = company.VatStatus == VatStatus.CzynnyPodatnikVat;
        var settings = new SettingsService(db, _session.Clock);
        var settled = await settings.GetAsync<VatService.VatSettlementSnapshot?>(companyId, $"vat.settlement.{from:yyyy-MM}", () => null) is { JournalEntryId: not null };
        var jpk = await settings.GetAsync<DateTime?>(companyId, $"jpk.wygenerowano.{from:yyyy-MM}", () => null);
        var quarterly = company.VatPeriod == VatSettlementPeriod.Kwartalny;
        Steps[3].Applicable = vatPayer && (!quarterly || from.Month % 3 == 0);
        Steps[4].Applicable = vatPayer;
        Steps[3].Done = !Steps[3].Applicable || settled;
        Steps[3].Status = !vatPayer ? "Nie dotyczy (brak rejestracji VAT)." : !Steps[3].Applicable ? "Rozliczenie kwartalne — VAT rozliczysz w ostatnim miesiącu kwartału." :
            settled ? "Rozliczenie VAT zaksięgowane." : quarterly ? "Rozliczenie VAT za kwartał niezaksięgowane." : "Rozliczenie VAT niezaksięgowane.";
        Steps[4].Done = !Steps[4].Applicable || jpk is not null;
        Steps[4].Status = !Steps[4].Applicable ? "Nie dotyczy." : jpk is { } j ? $"Wygenerowano {j.ToPlDateTime()}." : "Plik nie został wygenerowany.";

        var cit = LegalForms.IsCitTaxpayer(company.LegalForm) && (company.CitAdvanceMethod == CitAdvanceMethod.Miesieczne || period.Number % 3 == 0);
        var adv = await db.CitAdvances.AsNoTracking().FirstOrDefaultAsync(a => a.FiscalYearId == fy.Id && a.PeriodNumber == period.Number);
        Steps[5].Applicable = cit;
        Steps[5].Done = !cit || adv?.IsApproved == true;
        Steps[5].Status = !cit ? "Nie dotyczy w tym miesiącu." : adv is null ? "Zaliczka niewyliczona." : $"Zaliczka {Money.Format(adv.AdvanceDue)} zł, termin {adv.PaymentDeadline.ToPl()}{(adv.IsApproved ? " — zatwierdzona" : " — do zatwierdzenia")}.";

        var lastBackup = _session.Config.Backup.LastSuccessUtc;
        Steps[6].Done = lastBackup is { } lb && DateOnly.FromDateTime(SystemClock.ToWarsaw(lb)) > to;
        Steps[6].Status = lastBackup is { } l ? $"Ostatnia kopia: {l.ToPlDateTime()}." : "Brak kopii.";

        var p = (await db.Periods.AsNoTracking().FirstAsync(x => x.Id == period.Id));
        Steps[7].Done = p.Status == PeriodStatus.Zamkniety;
        Steps[7].Status = p.Status == PeriodStatus.Zamkniety ? "Miesiąc zamknięty." : "Miesiąc otwarty.";

        var left = Steps.Count(s => s.Applicable && !s.Done);
        Summary = left == 0 ? $"Miesiąc {Month} jest rozliczony i zamknięty." : $"Do wykonania: {left} z {Steps.Count(s => s.Applicable)} kroków. Wykonuj je po kolei.";
    }

    private async Task StepAsync(Func<Task> action)
    {
        await RunAsync(action);
        await RunAsync(RefreshCoreAsync);
    }

    private Task PostDocumentsAsync() => StepAsync(async () =>
    {
        await using var db0 = _session.OpenDb();
        var ids = await db0.Documents.AsNoTracking()
            .Where(d => d.CompanyId == _session.CompanyId && d.IssueDate >= MonthStart && d.IssueDate <= MonthEnd && (d.Status == DocumentStatus.Roboczy || d.Status == DocumentStatus.Zatwierdzony))
            .OrderBy(d => d.IssueDate).Select(d => new { d.Id, d.Number, d.Status }).ToListAsync();
        if (ids.Count == 0) { await Dialogs.InfoAsync("Dokumenty", "Brak dokumentów do zaksięgowania."); return; }
        if (!await Dialogs.ConfirmAsync("Księgowanie dokumentów", $"Zatwierdzić i zaksięgować {ids.Count} dokumentów z miesiąca {Month}? Dokumenty z błędami zostaną pominięte i wymienione.")) return;
        var ok = 0;
        var errors = new StringBuilder();
        foreach (var d in ids)
        {
            await using var db = _session.OpenDb();
            var svc = new DocumentService(db, _session.User, _session.Clock);
            try
            {
                if (d.Status == DocumentStatus.Roboczy)
                {
                    await svc.ApplyCategoryDeductionAsync(d.Id);
                    await svc.ApproveAsync(d.Id);
                }
                await svc.PostAsync(d.Id);
                ok++;
            }
            catch (DomainException ex) { errors.AppendLine($"• {d.Number}: {ex.Message}"); }
        }
        await Dialogs.ShowTextAsync("Księgowanie dokumentów", $"Zaksięgowano: {ok} z {ids.Count}." + (errors.Length > 0 ? Environment.NewLine + Environment.NewLine + "Pominięte (popraw je w sekcji Dokumenty, Sprzedaż lub Zakupy):" + Environment.NewLine + errors : ""));
    });

    private Task PostBankAsync() => StepAsync(async () =>
    {
        await using var db0 = _session.OpenDb();
        var ids = await db0.MoneyTransactions.AsNoTracking().Where(t => t.CompanyId == _session.CompanyId && t.Status == TransactionStatus.Dopasowana && t.BookingDate <= MonthEnd)
            .OrderBy(t => t.BookingDate).Select(t => t.Id).ToListAsync();
        var unmatched = await db0.MoneyTransactions.CountAsync(t => t.CompanyId == _session.CompanyId && t.Status == TransactionStatus.Nierozliczona && t.BookingDate <= MonthEnd);
        var ok = 0;
        var errors = new StringBuilder();
        foreach (var id in ids)
        {
            await using var db = _session.OpenDb();
            try { await new BankService(db, _session.User, _session.Clock).PostTransactionAsync(id); ok++; }
            catch (DomainException ex) { errors.AppendLine("• " + ex.Message); }
        }
        await Dialogs.ShowTextAsync("Bank i kasa", $"Zaksięgowano dopasowane operacje: {ok}." + (errors.Length > 0 ? Environment.NewLine + errors : "") +
            (unmatched > 0 ? $"{Environment.NewLine}{unmatched} operacji nie dopasowano — dopasuj je w sekcji „Bank i kasa” (do faktury albo konta, np. opłaty bankowe 751-03)." : ""));
        if (unmatched > 0) _session.Navigate(AppArea.Bank);
    });

    private Task DepreciationAsync() => StepAsync(async () =>
    {
        await using var db = _session.OpenDb();
        var r = await new FixedAssetService(db, _session.User, _session.Clock).RunDepreciationAsync(MonthStart.Year, MonthStart.Month);
        await Dialogs.InfoAsync("Amortyzacja", $"Naliczono amortyzację bilansową {Money.Format(r.TotalBook)} zł (podatkową {Money.Format(r.TotalTax)} zł).");
    });

    private Task VatAsync() => StepAsync(async () =>
    {
        await using var db = _session.OpenDb();
        var vat = new VatService(db, _session.User, _session.Clock);
        var problems = await vat.ValidateRegistersAsync(MonthStart);
        if (problems.Count > 0)
        {
            await Dialogs.ShowTextAsync("Kontrola rejestrów VAT", "Popraw najpierw:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
            return;
        }
        if (!await vat.IsDeclarationMonthAsync(MonthStart))
        {
            await Dialogs.InfoAsync("Rozliczenie VAT", "Rozliczasz VAT kwartalnie — w tym miesiącu nie rozliczasz podatku. Wygeneruj tylko JPK_V7K (sama ewidencja). Rozliczenie za kwartał zrobisz w ostatnim miesiącu kwartału.");
            return;
        }
        var decl = await vat.ComputeDeclarationAsync(MonthStart);
        if (!await Dialogs.ConfirmAsync(decl.Quarterly ? $"Rozliczenie VAT za {decl.Quarter} kwartał" : "Rozliczenie VAT", $"VAT należny: {Money.Format(decl.OutputTax)} zł, naliczony: {Money.Format(decl.InputTax)} zł.{Environment.NewLine}" +
                (decl.Payable > 0 ? $"Do zapłaty do urzędu: {Money.Format(decl.Payable)} zł." : $"Nadwyżka do przeniesienia: {Money.Format(decl.Surplus)} zł.") +
                $"{Environment.NewLine}{Environment.NewLine}Zaksięgować rozliczenie VAT za {Month}?")) return;
        await vat.PostSettlementAsync(MonthStart);
    });

    private Task JpkAsync() => StepAsync(async () => await JpkExport.GenerateAsync(_session, Dialogs, MonthStart, null, false));

    private Task CitAsync() => StepAsync(async () =>
    {
        var fy = _session.Years.First(y => y.Contains(MonthStart));
        var number = fy.Periods.First(p => p.Contains(MonthStart)).Number;
        await using var db = _session.OpenDb();
        var svc = new CitService(db, _session.User, _session.Clock);
        var adv = await svc.ComputeAdvanceAsync(fy.Id, number);
        var rec = await svc.ReconcileAsync(fy.Id, number);
        var analyze = rec.ToAnalyze.Count > 0 ? $"{Environment.NewLine}Pozycje do analizy podatkowej: {string.Join(", ", rec.ToAnalyze.Select(a => $"{a.Account} {a.Name} ({Money.Format(a.Amount)})"))}." : "";
        if (!await Dialogs.ConfirmAsync("Zaliczka CIT", $"Dochód od początku roku: {Money.Format(adv.CumulativeIncome)} zł, podatek: {Money.Format(adv.CumulativeTax)} zł.{Environment.NewLine}" +
                $"Zaliczka do wpłaty: {Money.Format(adv.AdvanceDue)} zł, termin {adv.PaymentDeadline.ToPl()}.{analyze}{Environment.NewLine}{Environment.NewLine}" +
                "Zatwierdzić wyliczenie? (Potwierdzasz, że pozycje do analizy zostały sprawdzone.)")) return;
        await svc.ApproveAdvanceAsync(adv.Id, true);
    });

    private Task BackupAsync() => StepAsync(async () =>
    {
        var r = await _session.BackupAsync($"po rozliczeniu miesiąca {Month}", upload: true);
        await Dialogs.InfoAsync("Kopia zapasowa", $"Utworzono i zweryfikowano kopię: {r.FilePath}");
    });

    private Task ClosePeriodAsync() => StepAsync(async () =>
    {
        var fy = _session.Years.First(y => y.Contains(MonthStart));
        var periodId = fy.Periods.First(p => p.Contains(MonthStart)).Id;
        await using var db = _session.OpenDb();
        var svc = new PeriodService(db, _session.User, _session.Clock);
        var c = await svc.BuildChecklistAsync(periodId);
        if (c.HasBlocking)
        {
            await Dialogs.ShowTextAsync("Zamknięcie miesiąca", "Nie można jeszcze zamknąć miesiąca:" + Environment.NewLine +
                string.Join(Environment.NewLine, c.Items.Where(i => !i.Passed && i.Severity == CheckSeverity.Blokujace).Select(i => $"• {i.Title}: {i.Details}")));
            return;
        }
        string? ack = null;
        if (c.NeedsAcknowledgement)
        {
            ack = await Dialogs.PromptAsync("Zamknięcie miesiąca", "Ostrzeżenia:" + Environment.NewLine +
                string.Join(Environment.NewLine, c.Items.Where(i => !i.Passed && i.Severity == CheckSeverity.WymagaPotwierdzenia).Select(i => $"• {i.Title}: {i.Details}")) +
                Environment.NewLine + Environment.NewLine + "Wpisz uzasadnienie zamknięcia mimo ostrzeżeń:");
            if (string.IsNullOrWhiteSpace(ack)) return;
        }
        else if (!await Dialogs.ConfirmAsync("Zamknięcie miesiąca", $"Zamknąć miesiąc {Month}?")) return;
        await svc.ClosePeriodAsync(periodId, ack);
        await _session.RefreshAsync();
    });
}
