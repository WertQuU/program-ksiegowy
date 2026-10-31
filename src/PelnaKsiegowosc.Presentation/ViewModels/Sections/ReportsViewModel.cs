using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Export;
using PelnaKsiegowosc.Infrastructure.Xml;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

public enum ReportKind
{
    Dziennik, KsiegaGlowna, ZestawienieObrotowISald, ZestawienieSaldKontPomocniczych, BilansOtwarcia, Bilans, RachunekZyskowIStrat, RachunekZyskowIStratKalkulacyjny,
    ZestawienieZmianWKapitale, RachunekPrzeplywow, InformacjaDodatkowa,
    RejestrVatSprzedazy, RejestrVatZakupow, Naleznosci, Zobowiazania, EwidencjaSrodkowTrwalych, PlanAmortyzacjiBilansowej, PlanAmortyzacjiPodatkowej,
    UzgodnienieBanku, UzgodnienieCit, DziennikZmian, KontrolaIntegralnosci, OpisSystemuInformatycznego
}

/// <summary>Raporty z ksiąg: podgląd i eksport do PDF, XLSX, CSV; sprawozdanie finansowe w XML.</summary>
public sealed partial class ReportsViewModel : SectionViewModel
{
    public ReportsViewModel(AppSession s, IDialogService d) : base(s, d, "Raporty", "📊")
    {
        Report = Reports[0];
    }

    public List<Choice<ReportKind>> Reports { get; } = Enum.GetValues<ReportKind>().Select(k => new Choice<ReportKind>(k, k switch
    {
        ReportKind.Dziennik => "Dziennik",
        ReportKind.KsiegaGlowna => "Księga główna (konto)",
        ReportKind.ZestawienieObrotowISald => "Zestawienie obrotów i sald",
        ReportKind.ZestawienieSaldKontPomocniczych => "Zestawienie sald kont pomocniczych (kontrahenci)",
        ReportKind.BilansOtwarcia => "Bilans otwarcia",
        ReportKind.Bilans => "Bilans",
        ReportKind.RachunekZyskowIStrat => "Rachunek zysków i strat (porównawczy)",
        ReportKind.RachunekZyskowIStratKalkulacyjny => "Rachunek zysków i strat (kalkulacyjny)",
        ReportKind.ZestawienieZmianWKapitale => "Zestawienie zmian w kapitale własnym",
        ReportKind.RachunekPrzeplywow => "Rachunek przepływów pieniężnych (pośredni)",
        ReportKind.InformacjaDodatkowa => "Informacja dodatkowa",
        ReportKind.RejestrVatSprzedazy => "Rejestr VAT sprzedaży",
        ReportKind.RejestrVatZakupow => "Rejestr VAT zakupów",
        ReportKind.Naleznosci => "Należności (wiekowanie)",
        ReportKind.Zobowiazania => "Zobowiązania (wiekowanie)",
        ReportKind.EwidencjaSrodkowTrwalych => "Ewidencja środków trwałych",
        ReportKind.PlanAmortyzacjiBilansowej => "Plan amortyzacji bilansowej (rok)",
        ReportKind.PlanAmortyzacjiPodatkowej => "Plan amortyzacji podatkowej (rok)",
        ReportKind.UzgodnienieBanku => "Uzgodnienie rachunków bankowych i kasy",
        ReportKind.UzgodnienieCit => "Uzgodnienie wyniku bilansowego z podatkowym",
        ReportKind.DziennikZmian => "Dziennik zdarzeń (audyt)",
        ReportKind.KontrolaIntegralnosci => "Kontrola integralności ksiąg",
        _ => "Opis systemu informatycznego (art. 10 ust. 1 pkt 3 lit. c UoR)"
    })).ToList();

    [ObservableProperty] private Choice<ReportKind> _report;
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private bool _overdueOnly;
    [ObservableProperty] private TableData _preview = TableData.Empty;
    [ObservableProperty] private string _reportHeader = "";
    private ReportDocument? _current;

    // Klucz podziału kosztów (RZiS kalkulacyjny)
    [ObservableProperty] private string _keyProduction = "";
    [ObservableProperty] private string _keySelling = "";
    [ObservableProperty] private string _keyAdmin = "";

    // Teksty informacji dodatkowej
    public List<Choice<string>> NotesSections { get; } = FinancialStatementNotes.Sections.Select(x => new Choice<string>(x.Key, x.Title)).ToList();
    [ObservableProperty] private Choice<string>? _notesSection;
    [ObservableProperty] private string _notesText = "";
    private Dictionary<string, string> _notes = new();
    [ObservableProperty] private bool _showCostKey;
    [ObservableProperty] private bool _showNotesEditor;

    partial void OnReportChanged(Choice<ReportKind> value)
    {
        ShowCostKey = value.Value == ReportKind.RachunekZyskowIStratKalkulacyjny;
        ShowNotesEditor = value.Value == ReportKind.InformacjaDodatkowa;
    }

    partial void OnNotesSectionChanging(Choice<string>? value)
    {
        if (NotesSection is { } old) _notes[old.Value] = NotesText;
    }

    partial void OnNotesSectionChanged(Choice<string>? value) => NotesText = value is null ? "" : _notes.GetValueOrDefault(value.Value, "");

    [RelayCommand]
    private Task SaveCostKeyAsync() => RunAsync(async () =>
    {
        var key = new CostAllocationKey(Parse.Amount(KeyProduction, "Koszt wytworzenia %"), Parse.Amount(KeySelling, "Koszty sprzedaży %"), Parse.Amount(KeyAdmin, "Koszty zarządu %"));
        await using var db = Session.OpenDb();
        await new ReportService(db, Session.User, Session.Clock).SaveCostAllocationKeyAsync(key);
        await Session.AuditAsync("KLUCZ_PODZIALU_KOSZTOW", "Setting", null, key.ToString());
        StatusMessage = $"Zapisano klucz podziału kosztów: {key}";
    });

    [RelayCommand]
    private Task SaveNotesAsync() => RunAsync(async () =>
    {
        if (NotesSection is { } cur) _notes[cur.Value] = NotesText;
        await using var db = Session.OpenDb();
        await new FinancialStatementNotes(new Application.Setup.SettingsService(db, Session.Clock), Session.CompanyId).SaveAsync(Session.CurrentYear.Name, _notes);
        await db.SaveChangesAsync();
        await Session.AuditAsync("INFORMACJA_DODATKOWA_ZAPISANA", "Setting", null, Session.CurrentYear.Name);
        StatusMessage = "Zapisano teksty informacji dodatkowej — wygeneruj raport, aby zobaczyć całość.";
    });

    protected override async Task LoadCoreAsync()
    {
        await using (var db = Session.OpenDb())
        {
            var key = await new ReportService(db, Session.User, Session.Clock).CostAllocationKeyAsync();
            KeyProduction = key.ProductionPercent.ToString("0.##");
            KeySelling = key.SellingPercent.ToString("0.##");
            KeyAdmin = key.AdministrationPercent.ToString("0.##");
            _notes = await new FinancialStatementNotes(new Application.Setup.SettingsService(db, Session.Clock), Session.CompanyId).GetAsync(Session.CurrentYear.Name);
        }
        NotesSection ??= NotesSections[0];
        NotesText = _notes.GetValueOrDefault(NotesSection.Value, "");
        var y = Session.CurrentYear;
        if (string.IsNullOrEmpty(From) || !y.Contains(Parse.OptionalDate(From, "Od") ?? default)) From = y.StartDate.ToPl();
        if (string.IsNullOrEmpty(To) || !y.Contains(Parse.OptionalDate(To, "Do") ?? default)) To = (Session.Clock.Today < y.EndDate && Session.Clock.Today >= y.StartDate ? Session.Clock.Today : y.EndDate).ToPl();
    }

    [RelayCommand]
    private Task GenerateAsync() => RunAsync(async () =>
    {
        var y = Session.CurrentYear;
        var from = Parse.Date(From, "Od");
        var to = Parse.Date(To, "Do / na dzień");
        await using var db = Session.OpenDb();
        var r = new ReportService(db, Session.User, Session.Clock);
        _current = Report.Value switch
        {
            ReportKind.Dziennik => await r.JournalAsync(y.Id, from, to),
            ReportKind.KsiegaGlowna => await r.GeneralLedgerAsync(y.Id, Parse.Trim(Account) ?? throw new DomainException("Podaj numer konta."), from, to),
            ReportKind.ZestawienieObrotowISald => await r.TrialBalanceAsync(y.Id, from, to),
            ReportKind.ZestawienieSaldKontPomocniczych => await r.SubsidiaryBalancesAsync(y.Id, to),
            ReportKind.BilansOtwarcia => await r.OpeningBalanceAsync(y.Id),
            ReportKind.Bilans => await r.BalanceSheetAsync(y.Id, to),
            ReportKind.RachunekZyskowIStrat => await r.ProfitAndLossAsync(y.Id, to),
            ReportKind.RachunekZyskowIStratKalkulacyjny => await r.ProfitAndLossCalculationAsync(y.Id, to),
            ReportKind.ZestawienieZmianWKapitale => await r.EquityChangesAsync(y.Id, to),
            ReportKind.RachunekPrzeplywow => await r.CashFlowAsync(y.Id, to),
            ReportKind.InformacjaDodatkowa => await r.NotesAsync(y.Id, to),
            ReportKind.RejestrVatSprzedazy => await r.VatRegisterAsync(VatRegisterType.Sprzedaz, new DateOnly(from.Year, from.Month, 1)),
            ReportKind.RejestrVatZakupow => await r.VatRegisterAsync(VatRegisterType.Zakup, new DateOnly(from.Year, from.Month, 1)),
            ReportKind.Naleznosci => await r.AgingAsync(OpenItemSide.Naleznosc, to, OverdueOnly),
            ReportKind.Zobowiazania => await r.AgingAsync(OpenItemSide.Zobowiazanie, to, OverdueOnly),
            ReportKind.EwidencjaSrodkowTrwalych => await r.FixedAssetsAsync(to),
            ReportKind.PlanAmortyzacjiBilansowej => await r.DepreciationScheduleAsync(to.Year, DepreciationBasis.Bilansowa),
            ReportKind.PlanAmortyzacjiPodatkowej => await r.DepreciationScheduleAsync(to.Year, DepreciationBasis.Podatkowa),
            ReportKind.UzgodnienieBanku => await r.BankReconciliationAsync(y.Id, to),
            ReportKind.UzgodnienieCit => await r.CitReconciliationAsync(y.Id, y.Periods.LastOrDefault(p => p.StartDate <= to)?.Number ?? 1),
            ReportKind.DziennikZmian => await r.ChangeLogAsync(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc)),
            ReportKind.KontrolaIntegralnosci => await r.IntegrityReportAsync(),
            _ => await r.SystemDescriptionAsync(Session.DatabaseDescription)
        };
        Preview = TableData.FromReport(_current);
        ReportHeader = $"{_current.Title} — {_current.PeriodDescription}" + string.Concat(_current.Filters.Select(f => $"; {f.Name}: {f.Value}"));
        StatusMessage = _current.ControlsPassed ? $"Wierszy: {_current.Rows.Count}" : "UWAGA: kontrole raportu nie zostały spełnione — sprawdź uwagi pod tabelą.";
    });

    [RelayCommand] private Task ExportPdfAsync() => ExportAsync(new PdfReportExporter());
    [RelayCommand] private Task ExportXlsxAsync() => ExportAsync(new XlsxReportExporter());
    [RelayCommand] private Task ExportCsvAsync() => ExportAsync(new CsvReportExporter());

    private Task ExportAsync(IReportExporter exporter) => RunAsync(async () =>
    {
        if (_current is null) throw new DomainException("Najpierw wygeneruj raport.");
        var name = string.Concat(_current.Title.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-');
        var target = await Dialogs.SaveFileAsync($"Eksport {exporter.Format}", $"{name}-{Session.Clock.Today:yyyy-MM-dd}{exporter.FileExtension}", new FileFilter(exporter.Format, "*" + exporter.FileExtension));
        if (target is null) return;
        await File.WriteAllBytesAsync(target, exporter.Export(_current));
        await Session.AuditAsync("RAPORT_WYEKSPORTOWANY", "Report", null, $"{_current.Title}; {exporter.Format}; {Path.GetFileName(target)}");
        StatusMessage = $"Zapisano {target}";
    });

    /// <summary>Sprawozdanie podlega obowiązkowi badania (art. 64 UoR) — pole P_9 e-Sprawozdania.</summary>
    [ObservableProperty] private bool _auditRequired;
    [ObservableProperty] private bool _inThousands;

    /// <summary>Średnioroczne zatrudnienie w etatach (umowy o pracę) — z ewidencji płacowej.</summary>
    private static async Task<decimal> AverageEmploymentAsync(Data.KsiegiDbContext db, DateOnly from, DateOnly to)
    {
        var employees = await db.Employees.AsNoTracking().Where(e => e.ContractType == Domain.Payroll.ContractType.UmowaOPrace).ToListAsync();
        decimal sum = 0;
        var months = 0;
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1), months++)
        {
            var end = m.AddMonths(1).AddDays(-1);
            sum += employees.Where(e => e.StartDate <= end && (e.EndDate == null || e.EndDate >= m)).Sum(e => e.WorkTimeFraction);
        }
        return months == 0 ? 0 : Math.Round(sum / months, 2);
    }

    private async Task<FinancialStatementXmlBuilder.IncomeTaxInfo> IncomeTaxAsync(Data.KsiegiDbContext db, Domain.Periods.FiscalYear y)
    {
        var rec = await new Application.Cit.CitService(db, Session.User, Session.Clock).ReconcileAsync(y.Id, y.Periods.Count);
        var plus = rec.Adjustments.Where(a => a.Amount > 0).Sum(a => a.Amount);
        var minus = -rec.Adjustments.Where(a => a.Amount < 0).Sum(a => a.Amount);
        return new FinancialStatementXmlBuilder.IncomeTaxInfo(rec.BookResultBeforeTax, 0, minus, 0, plus, 0, 0, rec.Deductions, 0, rec.Computation.TaxBase, rec.Computation.Tax);
    }

    [RelayCommand]
    private Task FinancialStatementXmlAsync() => RunAsync(async () =>
    {
        var y = Session.CurrentYear;
        await using var db = Session.OpenDb();
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == Session.CompanyId);
        var sb = new StatementBuilder(db);
        var prev = await sb.PreviousYearAsync(y.Id);
        var bs = await sb.BalanceSheetAsync(y.Id, y.EndDate);
        var pl = await sb.ProfitAndLossAsync(y.Id, y.EndDate);
        var bsPrev = prev is null ? null : await sb.BalanceSheetAsync(prev.Id, prev.EndDate);
        var plPrev = prev is null ? null : await sb.ProfitAndLossAsync(prev.Id, prev.EndDate);
        var policy = await db.AccountingPolicies.AsNoTracking().Where(p => p.CompanyId == Session.CompanyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync();
        var reports = new ReportService(db, Session.User, Session.Clock);
        var key = await reports.CostAllocationKeyAsync();
        var calc = policy?.ProfitAndLossVariant == Domain.Companies.ProfitAndLossVariant.Kalkulacyjny;
        var simplified = policy is { IsMicroEntity: true } or { IsSmallEntity: true };
        var scope = policy is { IsMicroEntity: true } ? StatementScope.Mikro : policy is { IsSmallEntity: true } ? StatementScope.Mala : StatementScope.Inna;
        var notesTexts = await new FinancialStatementNotes(new Application.Setup.SettingsService(db, Session.Clock), Session.CompanyId).GetAsync(y.Name);
        var notesPdf = new PdfReportExporter().Export(await reports.NotesAsync(y.Id, y.EndDate));
        var extras = new FinancialStatementXmlBuilder.Extras(
            calc ? await sb.ProfitAndLossCalculationAsync(y.Id, y.EndDate, key) : null,
            calc && prev is not null ? await sb.ProfitAndLossCalculationAsync(prev.Id, prev.EndDate, key) : null,
            simplified ? null : await sb.EquityChangesAsync(y.Id, y.EndDate),
            simplified || prev is null ? null : await sb.EquityChangesAsync(prev.Id, prev.EndDate),
            simplified ? null : await sb.CashFlowAsync(y.Id, y.EndDate),
            simplified || prev is null ? null : await sb.CashFlowAsync(prev.Id, prev.EndDate),
            notesTexts["kontynuacja"], notesTexts["zasady"], notesPdf, $"informacja-dodatkowa-{y.Name}.pdf",
            AverageEmployment: await AverageEmploymentAsync(db, y.StartDate, y.EndDate), AuditRequired: AuditRequired,
            IncomeTax: Domain.Companies.LegalForms.IsCitTaxpayer(company.LegalForm) ? await IncomeTaxAsync(db, y) : null,
            Scope: scope, InThousands: InThousands);
        var r = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, Session.Clock.Today, bs, bsPrev, pl, plPrev, Session.Validator, extras);

        // kompletność: plik „gotowy do podpisu” tylko bez braków; w przeciwnym razie wyłącznie wersja robocza
        var missing = FinancialStatementCompleteness.Check(bs, r, notesTexts.Values, notesTexts["kontynuacja"], notesTexts["zasady"],
            y.Status is Domain.Periods.FiscalYearStatus.Zamkniety or Domain.Periods.FiscalYearStatus.Zarchiwizowany, AuditRequired, scope);
        var draft = missing.Count > 0;
        if (AuditRequired && scope != StatementScope.Inna)
            await Dialogs.InfoAsync("Sprawozdanie podlega badaniu", "Jednostka mała/mikro podlegająca badaniu: struktura sprawozdania wynika z kategorii jednostki i polityki rachunkowości (zał. nr 4/5 UoR). Potwierdź z biegłym rewidentem zakres sprawozdania (zestawienie zmian w kapitale, rachunek przepływów) przed podpisaniem.");
        if (draft && !await Dialogs.ConfirmAsync("Sprawozdanie niekompletne",
                "Sprawozdania nie można oznaczyć jako gotowego do podpisu i złożenia:" + Environment.NewLine + string.Join(Environment.NewLine, missing.Select(m => "• " + m)) +
                Environment.NewLine + Environment.NewLine + "Zapisać WERSJĘ ROBOCZĄ (tylko do przeglądu, nie do podpisu ani KRS)?"))
            return;
        var target = await Dialogs.SaveFileAsync(draft ? "Sprawozdanie finansowe — WERSJA ROBOCZA (XML)" : "Sprawozdanie finansowe (XML)",
            draft ? $"ROBOCZA-NIE-DO-PODPISU-sprawozdanie-finansowe-{y.Name}.xml" : $"sprawozdanie-finansowe-{y.Name}.xml", new FileFilter("XML", "*.xml"));
        if (target is null) return;
        if (draft && !Path.GetFileName(target).StartsWith("ROBOCZA", StringComparison.OrdinalIgnoreCase))
            target = Path.Combine(Path.GetDirectoryName(target) ?? "", "ROBOCZA-NIE-DO-PODPISU-" + Path.GetFileName(target));
        r.Document.Save(target);
        await Session.AuditAsync(draft ? "SPRAWOZDANIE_WERSJA_ROBOCZA" : "SPRAWOZDANIE_GOTOWE", "FinancialStatement", y.Id.ToString(),
            draft ? $"{y.Name}: braki — {string.Join("; ", missing)}" : $"{y.Name}: kompletne, {r.Status}");
        await Dialogs.ShowTextAsync("Sprawozdanie finansowe XML", $"Zapisano {target}{Environment.NewLine}Status: {(draft ? "WERSJA ROBOCZA — NIE DO PODPISU" : "kompletne — " + r.Status)}{Environment.NewLine}{Environment.NewLine}" +
            (r.NotImplemented.Count > 0 ? "Elementy niewygenerowane / do wykonania poza programem:" + Environment.NewLine + string.Join(Environment.NewLine, r.NotImplemented) + Environment.NewLine : "") +
            string.Join(Environment.NewLine, r.Validation.Errors.Take(30)) +
            $"{Environment.NewLine}Struktura: {scope switch { StatementScope.Mikro => "SFJMI", StatementScope.Mala => "SFJMA", _ => "SFJIN" }}{(InThousands ? "T" : "Z")}(2) — {scope switch { StatementScope.Mikro => "jednostka mikro, zał. nr 4 UoR", StatementScope.Mala => "jednostka mała, zał. nr 5 UoR", _ => "zał. nr 1 UoR" }}, kwoty {(InThousands ? "w tysiącach złotych" : "w złotych")} (MF, sprawozdania sporządzane od 1.01.2026; uproszczenia wybiera się w polityce rachunkowości). Plik podpisz w aplikacji e-Sprawozdania MF i złóż w e-KRS (lub KAS dla podmiotów spoza KRS).");
    });
}
