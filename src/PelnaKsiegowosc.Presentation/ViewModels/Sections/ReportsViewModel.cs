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
    Dziennik, KsiegaGlowna, ZestawienieObrotowISald, ZestawienieSaldKontPomocniczych, BilansOtwarcia, Bilans, RachunekZyskowIStrat,
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

    protected override Task LoadCoreAsync()
    {
        var y = Session.CurrentYear;
        if (string.IsNullOrEmpty(From) || !y.Contains(Parse.OptionalDate(From, "Od") ?? default)) From = y.StartDate.ToPl();
        if (string.IsNullOrEmpty(To) || !y.Contains(Parse.OptionalDate(To, "Do") ?? default)) To = (Session.Clock.Today < y.EndDate && Session.Clock.Today >= y.StartDate ? Session.Clock.Today : y.EndDate).ToPl();
        return Task.CompletedTask;
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
        var r = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, Session.Clock.Today, bs, bsPrev, pl, plPrev, Session.Validator);
        var target = await Dialogs.SaveFileAsync("Sprawozdanie finansowe (XML)", $"sprawozdanie-finansowe-{y.Name}.xml", new FileFilter("XML", "*.xml"));
        if (target is null) return;
        r.Document.Save(target);
        await Dialogs.ShowTextAsync("Sprawozdanie finansowe XML", $"Zapisano {target}{Environment.NewLine}Status: {r.Status}{Environment.NewLine}{Environment.NewLine}" +
            (r.NotImplemented.Count > 0 ? "Elementy niewygenerowane (uzupełnij w innym narzędziu):" + Environment.NewLine + string.Join(Environment.NewLine, r.NotImplemented) + Environment.NewLine : "") +
            string.Join(Environment.NewLine, r.Validation.Errors.Take(30)) +
            $"{Environment.NewLine}Sprawozdanie wymaga informacji dodatkowej, podpisów kierownika jednostki i osoby sporządzającej oraz weryfikacji z aktualną strukturą logiczną.");
    });
}
