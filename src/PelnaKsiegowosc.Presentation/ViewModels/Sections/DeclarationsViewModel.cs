using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Export;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Pozycja katalogu deklaracji: co to jest, termin, gdzie w programie ją przygotować.</summary>
public sealed record DeclarationInfo(string Name, string Deadline, string Where, AppArea Area, string? Kind);

/// <summary>Jedno miejsce ze wszystkimi deklaracjami i dokumentami rocznymi — generowanie lub przejście do właściwej sekcji.</summary>
public sealed partial class DeclarationsViewModel : SectionViewModel
{
    public DeclarationsViewModel(AppSession s, IDialogService d) : base(s, d, "Deklaracje", "🧾") => _selected = Catalog[0];

    public List<DeclarationInfo> Catalog { get; } = new()
    {
        new("CIT-8 z CIT-8/O — zeznanie roczne", "koniec 3. miesiąca po roku", "tutaj: Generuj", AppArea.Cit, "CIT8"),
        new("VAT-UE — informacja podsumowująca", "25. dzień następnego miesiąca", "tutaj: Generuj (okres od–do)", AppArea.Vat, "VATUE"),
        new("IFT-2R / CIT-10Z — wypłaty dla nierezydentów", "koniec 3. miesiąca po roku", "tutaj: Generuj (rok)", AppArea.Cit, "IFT2R"),
        new("Uchwały: zatwierdzenie sprawozdania, podział zysku, absolutorium", "6 miesięcy po dniu bilansowym", "tutaj: Generuj", AppArea.Raporty, "UCHWALY"),
        new("JPK_V7M / JPK_V7K — ewidencja i deklaracja VAT", "25. dzień następnego miesiąca", "sekcja VAT → Generuj JPK", AppArea.Vat, null),
        new("JPK_CIT (JPK_KR_PD) — księgi ze znacznikami", "koniec 7. miesiąca po roku", "sekcja CIT → JPK CIT", AppArea.Cit, null),
        new("Zaliczki CIT", "20. dzień następnego miesiąca", "sekcja CIT → wyliczenie zaliczki", AppArea.Cit, null),
        new("ZUS DRA / RCA", "15. (osoby prawne) / 20. dzień", "sekcja Płace → ZUS i PIT", AppArea.Place, null),
        new("PIT-4R, PIT-8AR, PIT-11", "koniec stycznia (PIT-11 podatnikowi: koniec lutego)", "sekcja Płace → ZUS i PIT", AppArea.Place, null),
        new("Sprawozdanie finansowe (e-Sprawozdanie XML) i informacja dodatkowa", "3 miesiące po dniu bilansowym", "sekcja Raporty", AppArea.Raporty, null),
        new("Zestawienie zmian w kapitale, rachunek przepływów", "razem ze sprawozdaniem", "sekcja Raporty", AppArea.Raporty, null),
    };

    [ObservableProperty] private DeclarationInfo _selected;
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _profitDistribution = "";
    [ObservableProperty] private TableData _preview = TableData.Empty;
    private ReportDocument? _current;

    protected override Task LoadCoreAsync()
    {
        var today = Session.Clock.Today;
        if (string.IsNullOrEmpty(Year)) Year = Session.CurrentYear.Name;
        var last = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        if (string.IsNullOrEmpty(From)) From = $"{last.Month:00}.{last.Year}";
        if (string.IsNullOrEmpty(To)) To = From;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task GenerateAsync() => RunAsync(async () =>
    {
        if (Selected.Kind is null)
        {
            Session.Navigate(Selected.Area);
            return;
        }
        var y = Session.Years.FirstOrDefault(f => f.Name == Year.Trim()) ?? Session.CurrentYear;
        await using var db = Session.OpenDb();
        var r = new ReportService(db, Session.User, Session.Clock);
        _current = Selected.Kind switch
        {
            "CIT8" => await r.Cit8Async(y.Id),
            "VATUE" => await r.VatUeAsync(Parse.Month(From, "Od"), Parse.Month(To, "Do")),
            "IFT2R" => await r.ForeignPaymentsAsync(y.EndDate.Year),
            _ => await r.AnnualResolutionsAsync(y.Id, Parse.Trim(ProfitDistribution))
        };
        Preview = TableData.FromReport(_current);
        StatusMessage = _current.ControlsPassed ? _current.Title : "UWAGA: uzupełnij pozycje wskazane pod tabelą.";
        await Session.AuditAsync("DEKLARACJA_PRZYGOTOWANA", "Declaration", null, _current.Title);
    });

    [RelayCommand] private Task ExportPdfAsync() => ExportAsync(new PdfReportExporter());
    [RelayCommand] private Task ExportXlsxAsync() => ExportAsync(new XlsxReportExporter());

    private Task ExportAsync(IReportExporter exporter) => RunAsync(async () =>
    {
        if (_current is null) throw new DomainException("Najpierw wygeneruj dokument.");
        var name = string.Concat(_current.Title.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-');
        var target = await Dialogs.SaveFileAsync($"Zapisz {exporter.Format}", name + exporter.FileExtension, new FileFilter(exporter.Format, "*" + exporter.FileExtension));
        if (target is null) return;
        await File.WriteAllBytesAsync(target, exporter.Export(_current));
        StatusMessage = $"Zapisano {target}";
    });
}
