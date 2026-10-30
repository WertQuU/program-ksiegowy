using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Infrastructure.Declarations;
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
        new("VAT-UE — informacja podsumowująca", "25. dzień następnego miesiąca", "tutaj: Generuj (okres od–do), plik XML za miesiąc", AppArea.Vat, "VATUE"),
        new("PIT-11 — informacje o dochodach pracowników i zleceniobiorców", "do końca stycznia (US), do końca lutego (podatnik)", "tutaj: Generuj (rok), pliki XML", AppArea.Place, "PIT11"),
        new("IFT-2R / CIT-10Z — wypłaty dla nierezydentów", "koniec 3. miesiąca po roku", "tutaj: Generuj (rok)", AppArea.Cit, "IFT2R"),
        new("Uchwały: zatwierdzenie sprawozdania, podział zysku, absolutorium", "6 miesięcy po dniu bilansowym", "tutaj: Generuj", AppArea.Raporty, "UCHWALY"),
        new("PIT-36 / PIT-36L — właściciel JDG / wspólnicy spółki osobowej", "30 kwietnia (zaliczki: 20. dnia)", "tutaj: wpisz wspólników i Generuj", AppArea.Cit, "PIT"),
        new("Inwentaryzacja — składniki i metody (art. 26 UoR)", "koniec roku / do 15 stycznia", "tutaj: Generuj (na dzień)", AppArea.Zamkniecie, "INW"),
        new("Potwierdzenia sald dla kontrahentów", "IV kwartał — do 15 stycznia", "tutaj: Generuj (na dzień)", AppArea.Kontrahenci, "SALDA"),
        new("Protokół inwentaryzacji kasy", "ostatni dzień roku", "tutaj: konto kasy + stan policzony", AppArea.Bank, "KASA"),
        new("JPK_V7M / JPK_V7K — ewidencja i deklaracja VAT", "25. dzień następnego miesiąca", "sekcja VAT → Generuj JPK", AppArea.Vat, null),
        new("JPK_CIT (JPK_KR_PD) — księgi ze znacznikami", "koniec 7. miesiąca po roku", "sekcja CIT → JPK CIT", AppArea.Cit, null),
        new("Zaliczki CIT", "20. dzień następnego miesiąca", "sekcja CIT → wyliczenie zaliczki", AppArea.Cit, null),
        new("ZUS DRA / RCA", "15. (osoby prawne) / 20. dzień", "sekcja Płace → ZUS i PIT", AppArea.Place, null),
        new("PIT-4R, PIT-8AR", "koniec stycznia", "sekcja Płace → ZUS i PIT", AppArea.Place, null),
        new("Sprawozdanie finansowe (e-Sprawozdanie XML) i informacja dodatkowa", "3 miesiące po dniu bilansowym", "sekcja Raporty", AppArea.Raporty, null),
        new("Zestawienie zmian w kapitale, rachunek przepływów", "razem ze sprawozdaniem", "sekcja Raporty", AppArea.Raporty, null),
    };

    [ObservableProperty] private DeclarationInfo _selected;
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _profitDistribution = "";
    [ObservableProperty] private string _partners = "";
    [ObservableProperty] private string _asOf = "";
    [ObservableProperty] private string _cashAccount = "100";
    [ObservableProperty] private string _cashCounted = "";
    public bool ShowPartners => Selected.Kind == "PIT";
    public bool ShowInventory => Selected.Kind is "INW" or "SALDA" or "KASA";

    partial void OnSelectedChanged(DeclarationInfo value)
    {
        OnPropertyChanged(nameof(ShowPartners));
        OnPropertyChanged(nameof(ShowInventory));
    }
    [ObservableProperty] private TableData _preview = TableData.Empty;
    private ReportDocument? _current;

    protected override Task LoadCoreAsync()
    {
        var today = Session.Clock.Today;
        if (string.IsNullOrEmpty(Year)) Year = Session.CurrentYear.Name;
        var last = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        if (string.IsNullOrEmpty(From)) From = $"{last.Month:00}.{last.Year}";
        if (string.IsNullOrEmpty(To)) To = From;
        if (string.IsNullOrEmpty(AsOf)) AsOf = Session.CurrentYear.EndDate.ToPl();
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
            "PIT11" => await r.Pit11Async(y.EndDate.Year),
            "PIT" => await r.PitPartnersAsync(y.Id, Partners.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(PitPartner.ParseLine).ToList()),
            "INW" => await r.InventoryPlanAsync(y.Id, Parse.Date(AsOf, "Na dzień")),
            "SALDA" => await r.BalanceConfirmationsAsync(Parse.Date(AsOf, "Na dzień"), true),
            "KASA" => await r.CashCountAsync(y.Id, Parse.Date(AsOf, "Na dzień"), CashAccount.Trim(), Parse.Amount(CashCounted, "Stan policzony")),
            _ => await r.AnnualResolutionsAsync(y.Id, Parse.Trim(ProfitDistribution))
        };
        Preview = TableData.FromReport(_current);
        StatusMessage = _current.ControlsPassed ? _current.Title : "UWAGA: uzupełnij pozycje wskazane pod tabelą.";
        await Session.AuditAsync("DEKLARACJA_PRZYGOTOWANA", "Declaration", null, _current.Title);
    });

    [RelayCommand]
    private Task XmlAsync() => RunAsync(async () =>
    {
        var y = Session.Years.FirstOrDefault(f => f.Name == Year.Trim()) ?? Session.CurrentYear;
        await using var db = Session.OpenDb();
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == Session.CompanyId);
        var person = await DeclarationXmlExport.OwnerAsync(db, company);
        var r = new ReportService(db, Session.User, Session.Clock);
        switch (Selected.Kind)
        {
            case "VATUE":
                var month = Parse.Month(From, "Od");
                if (Parse.Month(To, "Do") != month) throw new DomainException("VAT-UE składa się za miesiąc — ustaw ten sam miesiąc w polach Od i Do.");
                var x = VatUeXmlBuilder.Build(company, month, await r.VatUeLinesAsync(month, month), Session.Validator, person);
                await DeclarationXmlExport.SaveAsync(Session, Dialogs, "VAT-UE(5)", $"VAT-UE-{month:yyyy-MM}.xml", x,
                    "Plik wyślij przez e-Deklaracje (bramka MF) lub e-Urząd Skarbowy — podpis przy wysyłce. Program nie wysyła deklaracji.");
                break;
            case "PIT11":
                var year = y.EndDate.Year;
                var people = await r.Pit11DataAsync(year);
                if (people.Count == 0) throw new DomainException($"Brak zaksięgowanych list płac w roku {year}.");
                var results = new List<(string Name, DeclarationXmlResult? Result, string? Error)>();
                foreach (var p in people)
                {
                    try { results.Add(($"{p.LastName} {p.FirstName}", Pit11XmlBuilder.Build(company, year, p, Session.Validator, person), null)); }
                    catch (DomainException ex) { results.Add(($"{p.LastName} {p.FirstName}", null, ex.Message)); }
                }
                var folder = await Dialogs.PickFolderAsync($"Folder na pliki PIT-11 za {year}");
                if (folder is null) return;
                var report = new List<string>();
                foreach (var (name, res, err) in results)
                {
                    if (res is null) { report.Add($"✖ {name}: {err}"); continue; }
                    var file = Path.Combine(folder, $"PIT-11-{year}-{string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-')}.xml");
                    res.Document.Save(file);
                    report.Add($"{(res.Validation.IsValid ? "✔" : "⚠")} {name}: {Path.GetFileName(file)} — {res.Status}");
                    report.AddRange(res.Validation.Errors.Take(5).Select(e => "    " + e));
                }
                await Session.AuditAsync("DEKLARACJA_XML", "Declaration", null, $"PIT-11 {year}; {results.Count(x => x.Result is not null)} plików");
                await Dialogs.ShowTextAsync("PIT-11 (XML)", string.Join(Environment.NewLine, report) + Environment.NewLine + Environment.NewLine +
                    "Pliki wyślij przez e-Deklaracje (pojedynczo lub zbiorczo przez bramkę UBD jako PIT-11Z) i przekaż podatnikom kopie. Sprawdź przypisanie przychodów do wierszy części E.");
                break;
            default:
                throw new DomainException(XmlUnavailable(Selected.Kind));
        }
    });

    private static string XmlUnavailable(string? kind) => kind switch
    {
        null => "Ten dokument przygotowuje się w innej sekcji — użyj przycisku „Generuj / przejdź”.",
        _ => "Dla tego dokumentu program przygotowuje zestawienie kwot (PDF/XLSX) — przepisz je do formularza w e-Urzędzie Skarbowym."
    };

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
