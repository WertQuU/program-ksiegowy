using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Warehouse;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Warehouse;
using PelnaKsiegowosc.Infrastructure.Export;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Magazyn: kartoteki, dokumenty PZ/WZ/RW/PW, różnice inwentaryzacyjne, stan i arkusz spisu z natury.</summary>
public sealed partial class StockViewModel : SectionViewModel
{
    public StockViewModel(AppSession s, IDialogService d) : base(s, d, "Magazyn", "📦")
    {
        _kind = Kinds[0];
        _movementType = MovementTypes[1];
    }

    public List<Choice<StockItemKind>> Kinds { get; } = new() { new(StockItemKind.Towar, "Towar (330 / 731)"), new(StockItemKind.Material, "Materiał (310 / 401)"), new(StockItemKind.Produkt, "Produkt (600 / 490 zmiana stanu)") };
    public List<Choice<StockMovementType>> MovementTypes { get; } = new()
    {
        new(StockMovementType.PZ, "PZ — przyjęcie (zakup)"), new(StockMovementType.WZ, "WZ — wydanie (sprzedaż)"), new(StockMovementType.RW, "RW — zużycie wewnętrzne"),
        new(StockMovementType.PW, "PW — przyjęcie z produkcji"), new(StockMovementType.InwNiedobor, "Niedobór inwentaryzacyjny"), new(StockMovementType.InwNadwyzka, "Nadwyżka inwentaryzacyjna")
    };

    [ObservableProperty] private TableData _items = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedItem;
    [ObservableProperty] private TableData _movements = TableData.Empty;
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _unit = "szt.";
    [ObservableProperty] private Choice<StockItemKind> _kind;
    [ObservableProperty] private Choice<StockMovementType> _movementType;
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _quantity = "";
    [ObservableProperty] private string _price = "";
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _offsetAccount = "";
    [ObservableProperty] private string _asOf = "";
    [ObservableProperty] private TableData _report = TableData.Empty;
    private ReportDocument? _current;

    protected override async Task LoadCoreAsync()
    {
        if (string.IsNullOrEmpty(Date)) Date = Session.Clock.Today.ToPl();
        if (string.IsNullOrEmpty(AsOf)) AsOf = Session.Clock.Today.ToPl();
        await using var db = Session.OpenDb();
        var t = new TableData(new TableColumn("Kod"), new TableColumn("Nazwa", Width: 2), new TableColumn("J.m.", Width: 0.5), new TableColumn("Konto"),
            new TableColumn("Ilość", ColumnKind.Decimal), new TableColumn("Cena przeciętna", ColumnKind.Decimal), new TableColumn("Wartość", ColumnKind.Money));
        foreach (var i in await db.StockItems.AsNoTracking().Where(i => i.CompanyId == Session.CompanyId).OrderBy(i => i.Code).ToListAsync())
            t.Add(i.Id, i.Code, i.Name, i.Unit, i.InventoryAccount, i.Quantity, i.AveragePrice, i.Value);
        Items = t;
        var m = new TableData(new TableColumn("Numer"), new TableColumn("Data", ColumnKind.Date), new TableColumn("Pozycja", Width: 2), new TableColumn("Ilość", ColumnKind.Decimal),
            new TableColumn("Cena", ColumnKind.Decimal), new TableColumn("Wartość", ColumnKind.Money), new TableColumn("Stan po", ColumnKind.Decimal), new TableColumn("Dokument źródłowy", Width: 1.4));
        var names = await db.StockItems.AsNoTracking().Where(i => i.CompanyId == Session.CompanyId).ToDictionaryAsync(i => i.Id, i => i.Name);
        foreach (var x in await db.StockMovements.AsNoTracking().Where(x => x.CompanyId == Session.CompanyId).OrderByDescending(x => x.Date).ThenByDescending(x => x.CreatedAtUtc).Take(300).ToListAsync())
            m.Add(x.Id, x.Number, x.Date, names.GetValueOrDefault(x.StockItemId), x.Quantity, x.UnitPrice, x.Value, x.QuantityAfter, x.Reference);
        Movements = m;
    }

    [RelayCommand]
    private Task AddItemAsync() => ChangeAsync(async () =>
    {
        var (inv, issue) = Kind.Value switch { StockItemKind.Material => ("310", "401"), StockItemKind.Produkt => ("600", "490"), _ => ("330", "731") };
        await using var db = Session.OpenDb();
        await new StockService(db, Session.User, Session.Clock).AddItemAsync(new StockItem { Code = Code, Name = Name, Unit = Unit, Kind = Kind.Value, InventoryAccount = inv, IssueAccount = issue });
        Code = Name = "";
    }, "Dodano pozycję magazynową.");

    [RelayCommand]
    private Task RegisterAsync() => ChangeAsync(async () =>
    {
        if (SelectedItem?.Tag is not Guid id) throw new DomainException("Zaznacz pozycję magazynową.");
        await using var db = Session.OpenDb();
        var mv = await new StockService(db, Session.User, Session.Clock).RegisterAsync(MovementType.Value, id, Parse.Date(Date, "Data"), Parse.Amount(Quantity, "Ilość"),
            Parse.OptionalAmount(Price, "Cena"), Parse.Trim(Reference), null, Parse.Trim(OffsetAccount));
        Quantity = Price = Reference = "";
        StatusMessage = $"Zapisano {mv.Number}: wartość {Money.Format(mv.Value)} zł" + (mv.JournalEntryId is null ? " (bez zapisu księgowego)" : " — zaksięgowano.");
    }, "Zapisano dokument magazynowy.");

    private Task ShowAsync(bool sheet) => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        _current = await new ReportService(db, Session.User, Session.Clock).StockStateAsync(Parse.Date(AsOf, "Na dzień"), sheet);
        Report = TableData.FromReport(_current);
    });

    [RelayCommand] private Task StateAsync() => ShowAsync(false);
    [RelayCommand] private Task CountSheetAsync() => ShowAsync(true);

    [RelayCommand]
    private Task ExportPdfAsync() => RunAsync(async () =>
    {
        if (_current is null) throw new DomainException("Najpierw wygeneruj zestawienie.");
        var target = await Dialogs.SaveFileAsync("Zapisz PDF", $"{_current.Title.Replace(' ', '-')}.pdf", new FileFilter("PDF", "*.pdf"));
        if (target is null) return;
        await File.WriteAllBytesAsync(target, new PdfReportExporter().Export(_current));
        StatusMessage = $"Zapisano {target}";
    });
}
