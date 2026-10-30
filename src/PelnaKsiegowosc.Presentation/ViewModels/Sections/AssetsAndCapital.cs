using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Capital;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Ewidencja środków trwałych i WNiP: przyjęcie (OT), amortyzacja miesięczna, ulepszenie, likwidacja/sprzedaż.</summary>
public sealed partial class FixedAssetsViewModel : SectionViewModel
{
    public FixedAssetsViewModel(AppSession s, IDialogService d) : base(s, d, "Środki trwałe", "🏭") { }

    [ObservableProperty] private TableData _assets = TableData.Empty;
    [ObservableProperty] private TableRow? _selected;
    [ObservableProperty] private string _depreciationMonth = "";

    [ObservableProperty] private string _inventoryNumber = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Choice<FixedAssetCategory> _category = Choices.Of<FixedAssetCategory>()[0];
    [ObservableProperty] private string _kst = "";
    [ObservableProperty] private string _acquisitionDate = "";
    [ObservableProperty] private string _serviceDate = "";
    [ObservableProperty] private string _valueBook = "";
    [ObservableProperty] private string _valueTax = "";
    [ObservableProperty] private Choice<DepreciationMethod> _bookMethod = Choices.Of<DepreciationMethod>()[1];
    [ObservableProperty] private string _bookRate = "";
    [ObservableProperty] private Choice<DepreciationMethod> _taxMethod = Choices.Of<DepreciationMethod>()[1];
    [ObservableProperty] private string _taxRate = "";
    [ObservableProperty] private string _taxFactor = "2";
    [ObservableProperty] private string _nonDeductiblePercent = "0";
    [ObservableProperty] private string _assetAccount = "010-05";
    [ObservableProperty] private string _accumulatedAccount = "070-05";
    [ObservableProperty] private string _expenseAccount = "400";
    [ObservableProperty] private string _offsetAccount = "";
    [ObservableProperty] private CounterpartyChoice? _supplier;
    [ObservableProperty] private List<CounterpartyChoice> _counterparties = new();
    [ObservableProperty] private string _location = "";
    public List<Choice<FixedAssetCategory>> Categories { get; } = Choices.Of<FixedAssetCategory>();
    public List<Choice<DepreciationMethod>> Methods { get; } = Choices.Of<DepreciationMethod>();

    [ObservableProperty] private string _operationDate = "";
    [ObservableProperty] private string _improvementAmount = "";
    [ObservableProperty] private string _improvementOffset = "";
    [ObservableProperty] private string _operationReason = "";

    private Guid SelectedId => Selected?.Tag is Guid g ? g : throw new DomainException("Zaznacz środek trwały na liście.");

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var svc = new FixedAssetService(db, Session.User, Session.Clock);
        var today = Session.Clock.Today;
        if (string.IsNullOrEmpty(DepreciationMonth)) DepreciationMonth = $"{today.Month:00}.{today.Year}";
        if (string.IsNullOrEmpty(AcquisitionDate)) AcquisitionDate = ServiceDate = OperationDate = today.ToPl();
        Counterparties = await CounterpartyChoice.LoadAsync(db, Session.CompanyId);
        var list = await db.FixedAssets.AsNoTracking().Where(a => a.CompanyId == Session.CompanyId).OrderBy(a => a.InventoryNumber).ToListAsync();
        var t = new TableData(new TableColumn("Nr inw.", Width: 0.8), new TableColumn("Nazwa", Width: 2.2), new TableColumn("KŚT", Width: 0.5), new TableColumn("Przyjęty", ColumnKind.Date, 0.8),
            new TableColumn("Wartość początkowa", ColumnKind.Money), new TableColumn("Umorzenie bilansowe", ColumnKind.Money), new TableColumn("Wartość netto", ColumnKind.Money),
            new TableColumn("Umorzenie podatkowe", ColumnKind.Money), new TableColumn("Metoda", Width: 0.7), new TableColumn("Status", Width: 0.9));
        foreach (var a in list)
        {
            var (gross, accBook, accTax, net) = await svc.ValuesAsOfAsync(a.Id, today);
            t.Add(a.Id, a.InventoryNumber, a.Name, a.KstSymbol, a.PlacedInServiceDate, gross, accBook, net, accTax, a.BookMethod, a.Status);
        }
        Assets = t;
    }

    [RelayCommand]
    private Task RegisterAsync() => ChangeAsync(async () =>
    {
        var bookRate = Parse.Amount(BookRate, "Stawka bilansowa");
        var asset = new FixedAsset
        {
            InventoryNumber = InventoryNumber.Trim(), Name = Name.Trim(), Category = Category.Value, KstSymbol = Parse.Trim(Kst),
            AcquisitionDate = Parse.Date(AcquisitionDate, "Data nabycia"), PlacedInServiceDate = Parse.Date(ServiceDate, "Data przyjęcia do używania"),
            InitialValueBook = Parse.Amount(ValueBook, "Wartość początkowa bilansowa"),
            InitialValueTax = string.IsNullOrWhiteSpace(ValueTax) ? Parse.Amount(ValueBook, "Wartość początkowa") : Parse.Amount(ValueTax, "Wartość początkowa podatkowa"),
            BookMethod = BookMethod.Value, BookRatePercent = bookRate,
            TaxMethod = TaxMethod.Value, TaxRatePercent = string.IsNullOrWhiteSpace(TaxRate) ? bookRate : Parse.Amount(TaxRate, "Stawka podatkowa"),
            TaxDegressiveFactor = Parse.Amount(TaxFactor, "Współczynnik degresywny"), TaxNonDeductiblePercent = Parse.Amount(NonDeductiblePercent, "Część NKUP"),
            AssetAccountNumber = AssetAccount.Trim(), AccumulatedDepreciationAccountNumber = AccumulatedAccount.Trim(), ExpenseAccountNumber = ExpenseAccount.Trim(),
            Location = Parse.Trim(Location)
        };
        await using var db = Session.OpenDb();
        await new FixedAssetService(db, Session.User, Session.Clock).RegisterAsync(asset, Parse.Trim(OffsetAccount), Supplier?.Id);
        InventoryNumber = Name = ValueBook = ValueTax = "";
    }, "Przyjęto środek trwały do ewidencji.");

    [RelayCommand]
    private Task RunDepreciationAsync() => ChangeAsync(async () =>
    {
        var m = Parse.Month(DepreciationMonth, "Miesiąc amortyzacji");
        if (!await Dialogs.ConfirmAsync("Amortyzacja", $"Naliczyć i zaksięgować amortyzację za {m:MM.yyyy}?")) return;
        await using var db = Session.OpenDb();
        var r = await new FixedAssetService(db, Session.User, Session.Clock).RunDepreciationAsync(m.Year, m.Month);
        await Dialogs.InfoAsync("Amortyzacja", $"Amortyzacja bilansowa: {Money.Format(r.TotalBook)} zł; podatkowa: {Money.Format(r.TotalTax)} zł.");
    }, "Naliczono amortyzację.");

    [RelayCommand]
    private Task ImproveAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        await using var db = Session.OpenDb();
        await new FixedAssetService(db, Session.User, Session.Clock).AddImprovementAsync(id, Parse.Date(OperationDate, "Data"), Parse.Amount(ImprovementAmount, "Kwota ulepszenia"),
            Parse.Trim(ImprovementOffset) ?? throw new DomainException("Podaj konto przeciwstawne ulepszenia (np. 080 lub 202)."),
            string.IsNullOrWhiteSpace(OperationReason) ? "Ulepszenie" : OperationReason.Trim());
    }, "Zaksięgowano ulepszenie.");

    [RelayCommand] private Task DisposeAssetAsync() => DisposeCoreAsync(false);
    [RelayCommand] private Task SellAssetAsync() => DisposeCoreAsync(true);

    private Task DisposeCoreAsync(bool sale) => ChangeAsync(async () =>
    {
        var id = SelectedId;
        if (string.IsNullOrWhiteSpace(OperationReason)) throw new DomainException("Podaj przyczynę/podstawę (np. protokół likwidacji, numer faktury sprzedaży).");
        if (!await Dialogs.ConfirmAsync(sale ? "Sprzedaż" : "Likwidacja", $"{(sale ? "Wyksięgować sprzedany" : "Zlikwidować")} środek {Selected!.Cells[0]} z datą {OperationDate}?")) return;
        await using var db = Session.OpenDb();
        await new FixedAssetService(db, Session.User, Session.Clock).DisposeAsync(id, Parse.Date(OperationDate, "Data"), sale, OperationReason.Trim());
    }, sale ? "Wyksięgowano sprzedany środek trwały." : "Zlikwidowano środek trwały.");
}

/// <summary>Kapitały i właściciele: wspólnicy/akcjonariusze, zdarzenia kapitałowe, podział wyniku, dywidendy, pożyczki.</summary>
public sealed partial class CapitalViewModel : SectionViewModel
{
    public CapitalViewModel(AppSession s, IDialogService d) : base(s, d, "Kapitał i właściciele", "⚖") { }

    [ObservableProperty] private string _formInfo = "";
    [ObservableProperty] private TableData _owners = TableData.Empty;
    [ObservableProperty] private TableData _events = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedEvent;
    [ObservableProperty] private TableData _summary = TableData.Empty;
    [ObservableProperty] private List<Choice<Guid>> _ownerChoices = new();
    [ObservableProperty] private List<Choice<CapitalEventType>> _eventTypes = new();

    [ObservableProperty] private string _ownerName = "";
    [ObservableProperty] private string _ownerIdentifier = "";
    [ObservableProperty] private bool _ownerLegalPerson;

    [ObservableProperty] private Choice<CapitalEventType>? _eventType;
    [ObservableProperty] private Choice<Guid>? _eventOwner;
    [ObservableProperty] private string _eventDate = "";
    [ObservableProperty] private string _shareSeries = "";
    [ObservableProperty] private string _shareCount = "";
    [ObservableProperty] private string _shareCapitalAmount = "";
    [ObservableProperty] private string _supplementaryAmount = "";
    [ObservableProperty] private string _totalAmount = "";
    [ObservableProperty] private string _withholdingTax = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _debitAccount = "";
    [ObservableProperty] private string _creditAccount = "";
    [ObservableProperty] private string _resolution = "";
    [ObservableProperty] private string _resolutionDate = "";
    [ObservableProperty] private string _agreement = "";
    [ObservableProperty] private string _krsEntry = "";
    [ObservableProperty] private bool _payoutConfirmed;
    [ObservableProperty] private string _payoutRule = "";

    public static string EventName(CapitalEventType t, LegalForm f)
    {
        var shares = LegalForms.SharesName(f) ?? "wkładów";
        var cap = LegalForms.ShareCapitalName(f).ToLower(Money.Pl);
        var owner = LegalForms.OwnerName(f);
        return t switch
        {
            CapitalEventType.ObjecieAkcjiWkladPieniezny => $"Objęcie {shares} / wniesienie wkładu — wkład pieniężny (należny)",
            CapitalEventType.ObjecieAkcjiWkladNiepieniezny => "Wkład niepieniężny (aport)",
            CapitalEventType.WplataWkladu => "Wpłata należnego wkładu",
            CapitalEventType.PodwyzszenieKapitaluAkcyjnego => $"Podwyższenie: {cap} (ze środków własnych)",
            CapitalEventType.ObnizenieKapitaluAkcyjnego => $"Obniżenie: {cap}",
            CapitalEventType.PrzeniesienieNaKapitalZapasowy => "Przeniesienie wyniku na kapitał zapasowy",
            CapitalEventType.UtworzenieKapitaluRezerwowego => "Utworzenie kapitału rezerwowego",
            CapitalEventType.PodzialZysku => "Podział zysku",
            CapitalEventType.PokrycieStraty => "Pokrycie straty",
            CapitalEventType.UchwalenieDywidendy => $"Przeznaczenie zysku do wypłaty ({owner}) — dywidenda / udział w zysku",
            CapitalEventType.WyplataDywidendy => "Wypłata dywidendy / udziału w zysku",
            CapitalEventType.ZaliczkaNaPoczetDywidendy => "Zaliczka na poczet dywidendy / zysku",
            CapitalEventType.PozyczkaOdAkcjonariusza => $"Pożyczka od właściciela ({owner})",
            CapitalEventType.SplataPozyczkiAkcjonariusza => "Spłata pożyczki właściciela",
            _ => "Inne (księgowane poleceniem księgowania)"
        };
    }

    protected override async Task LoadCoreAsync()
    {
        var form = Session.Company!.LegalForm;
        FormInfo = $"Forma prawna: {LegalForms.Name(form)}. Kapitał podstawowy: {LegalForms.ShareCapitalName(form)}; właściciele: {LegalForms.OwnerName(form)}. " +
                   "Moduł ewidencjonuje skutki księgowe — nie zastępuje księgi udziałów, rejestru akcjonariuszy ani dokumentów korporacyjnych.";
        PayoutRule = "Wypłaty na rzecz właścicieli wymagają potwierdzenia: " + LegalForms.PayoutRule(form);
        EventTypes = Enum.GetValues<CapitalEventType>().Select(t => new Choice<CapitalEventType>(t, EventName(t, form))).ToList();
        EventType ??= EventTypes[0];
        if (string.IsNullOrEmpty(EventDate)) EventDate = Session.Clock.Today.ToPl();
        await using var db = Session.OpenDb();
        var owners = await db.Shareholders.AsNoTracking().Where(s => s.CompanyId == Session.CompanyId).OrderBy(s => s.Name).ToListAsync();
        var ot = new TableData(new TableColumn("Nazwa / imię i nazwisko", Width: 2), new TableColumn("Identyfikator"), new TableColumn("Osoba prawna", Width: 0.5));
        foreach (var o in owners) ot.Add(o.Id, o.Name, o.Identifier, o.IsLegalPerson);
        Owners = ot;
        OwnerChoices = owners.Select(o => new Choice<Guid>(o.Id, o.Name)).ToList();
        var names = owners.ToDictionary(o => o.Id, o => o.Name);
        var et = new TableData(new TableColumn("Data", ColumnKind.Date, 0.7), new TableColumn("Zdarzenie", Width: 2.5), new TableColumn("Właściciel", Width: 1.5),
            new TableColumn("Kapitał podstawowy", ColumnKind.Money), new TableColumn("Kwota", ColumnKind.Money), new TableColumn("Status", Width: 1), new TableColumn("Brakujące dokumenty", Width: 2.5));
        foreach (var e in await db.CapitalEvents.AsNoTracking().Where(e => e.CompanyId == Session.CompanyId).OrderByDescending(e => e.EventDate).ToListAsync())
            et.Add(e.Id, e.EventDate, EventName(e.Type, form), e.ShareholderId is { } sid ? names.GetValueOrDefault(sid) : null, e.ShareCapitalAmount, e.TotalAmount, e.Status,
                e.Status == CapitalEventStatus.Roboczy ? string.Join("; ", e.MissingDocuments(form)) : "");
        Events = et;
        var st = new TableData(new TableColumn("Konto", Width: 0.6), new TableColumn("Nazwa", Width: 2.5), new TableColumn("Saldo (Ma +)", ColumnKind.Money));
        foreach (var (acc, name, bal) in await new CapitalService(db, Session.User, Session.Clock).CapitalSummaryAsync(Session.CurrentYear.Id, Session.Clock.Today < Session.CurrentYear.EndDate ? Session.Clock.Today : Session.CurrentYear.EndDate))
            st.Add(null, acc, name, bal);
        if (form == LegalForm.ProstaSpolkaAkcyjna)
            foreach (var (holder, unpaid) in await new CapitalService(db, Session.User, Session.Clock).UnpaidContributionsAsync())
                st.Add(null, "poza bilansem", $"Wkład objęty, niewniesiony — {holder} (nie stanowi kapitału akcyjnego, art. 300³ KSH)", unpaid);
        Summary = st;
    }

    [RelayCommand]
    private Task AddOwnerAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new CapitalService(db, Session.User, Session.Clock).AddShareholderAsync(OwnerName, Parse.Trim(OwnerIdentifier), OwnerLegalPerson);
        OwnerName = OwnerIdentifier = "";
    }, "Dodano właściciela (utworzono też kontrahenta powiązanego).");

    [RelayCommand]
    private Task CreateEventAsync() => ChangeAsync(async () =>
    {
        if (EventType is null) throw new DomainException("Wybierz rodzaj zdarzenia.");
        var ev = new CapitalEvent
        {
            Type = EventType.Value, ShareholderId = EventOwner?.Value, EventDate = Parse.Date(EventDate, "Data zdarzenia"), ShareSeries = Parse.Trim(ShareSeries),
            ShareCount = string.IsNullOrWhiteSpace(ShareCount) ? null : Parse.Int(ShareCount, "Liczba akcji/udziałów"),
            ShareCapitalAmount = Parse.Amount(ShareCapitalAmount, "Kwota na kapitał podstawowy"), SupplementaryCapitalAmount = Parse.Amount(SupplementaryAmount, "Kwota na kapitał zapasowy"),
            TotalAmount = Parse.Amount(TotalAmount, "Kwota ogółem"), WithholdingTaxAmount = Parse.Amount(WithholdingTax, "Podatek pobrany"),
            ContributionType = EventType.Value switch
            {
                CapitalEventType.ObjecieAkcjiWkladPieniezny => ContributionType.Pieniezny,
                CapitalEventType.ObjecieAkcjiWkladNiepieniezny => ContributionType.Niepieniezny,
                _ => null
            },
            Description = Description.Trim(), DebitAccountNumber = Parse.Trim(DebitAccount), CreditAccountNumber = Parse.Trim(CreditAccount),
            ResolutionReference = Parse.Trim(Resolution), ResolutionDate = Parse.OptionalDate(ResolutionDate, "Data uchwały"), AgreementReference = Parse.Trim(Agreement),
            KrsEntryReference = Parse.Trim(KrsEntry), SolvencyTestConfirmed = PayoutConfirmed
        };
        await using var db = Session.OpenDb();
        await new CapitalService(db, Session.User, Session.Clock).CreateEventAsync(ev);
    }, "Zapisano zdarzenie kapitałowe (robocze) — potwierdź dokumenty i zaksięguj.");

    [RelayCommand]
    private Task ConfirmDocumentsAsync() => ChangeAsync(async () =>
    {
        if (SelectedEvent?.Tag is not Guid id) throw new DomainException("Zaznacz zdarzenie.");
        if (!await Dialogs.ConfirmAsync("Dokumenty korporacyjne", "Potwierdzasz, że wskazane dokumenty (uchwała, umowa, wpis do KRS, potwierdzenie dopuszczalności wypłaty) istnieją i są prawidłowe?")) return;
        await using var db = Session.OpenDb();
        await new CapitalService(db, Session.User, Session.Clock).ConfirmDocumentsAsync(id);
    }, "Potwierdzono dokumenty.");

    [RelayCommand]
    private Task PostEventAsync() => ChangeAsync(async () =>
    {
        if (SelectedEvent?.Tag is not Guid id) throw new DomainException("Zaznacz zdarzenie.");
        await using var db = Session.OpenDb();
        var e = await new CapitalService(db, Session.User, Session.Clock).PostEventAsync(id);
        StatusMessage = e is null
            ? "P.S.A.: objęcie akcji ujęto w ewidencji wkładów do wniesienia — kapitał akcyjny zostanie zaksięgowany przy wpłacie wkładu (art. 300³ KSH)."
            : $"Zaksięgowano {e.JournalNumber}";
    }, "Zdarzenie zaksięgowane.");
}
