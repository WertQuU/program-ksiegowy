using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Kartoteka kontrahentów (dane osobowe osób fizycznych — minimalizacja zgodnie z RODO).</summary>
public sealed partial class CounterpartiesViewModel : SectionViewModel
{
    public CounterpartiesViewModel(AppSession s, IDialogService d) : base(s, d, "Kontrahenci", "👥") { }

    [ObservableProperty] private TableData _list = TableData.Empty;
    [ObservableProperty] private TableRow? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showInactive;
    private Guid? _editedId;

    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Choice<CounterpartyKind> _kind = KindChoices[0];
    [ObservableProperty] private string _nip = "";
    [ObservableProperty] private string _vatEu = "";
    [ObservableProperty] private string _country = "PL";
    [ObservableProperty] private string _regon = "";
    [ObservableProperty] private string _street = "";
    [ObservableProperty] private string _building = "";
    [ObservableProperty] private string _apartment = "";
    [ObservableProperty] private string _postalCode = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _iban = "";
    [ObservableProperty] private string _paymentDays = "14";
    [ObservableProperty] private bool _relatedParty;
    [ObservableProperty] private bool _active = true;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _formHeader = "Nowy kontrahent";
    private static readonly List<Choice<CounterpartyKind>> KindChoices = Enum.GetValues<CounterpartyKind>()
        .Select(k => new Choice<CounterpartyKind>(k, KindName(k))).ToList();
    public List<Choice<CounterpartyKind>> Kinds => KindChoices;

    public static string KindName(CounterpartyKind k) => k == CounterpartyKind.Akcjonariusz ? "Wspólnik / akcjonariusz / właściciel" : Display.Enum(k);

    [ObservableProperty] private TableData _openItems = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedOpenItem;
    [ObservableProperty] private string _compensationAmount = "";
    [ObservableProperty] private string _compensationNote = "";

    private async Task LoadOpenItemsAsync(Guid counterpartyId)
    {
        await using var db = Session.OpenDb();
        var svc = new SettlementService(db, Session.User, Session.Clock);
        var t = new TableData(new TableColumn("Strona", Width: 0.9), new TableColumn("Dokument", Width: 1.4), new TableColumn("Konto", Width: 0.7), new TableColumn("Data", ColumnKind.Date, 0.8),
            new TableColumn("Termin", ColumnKind.Date, 0.8), new TableColumn("Kwota", ColumnKind.Money), new TableColumn("Pozostało (PLN)", ColumnKind.Money), new TableColumn("Dni po terminie", ColumnKind.Integer, 0.6));
        foreach (var side in new[] { OpenItemSide.Naleznosc, OpenItemSide.Zobowiazanie })
            foreach (var r in await svc.AgingAsync(side, Session.Clock.Today, false, counterpartyId))
                t.Add((side, r.OpenItemId), side == OpenItemSide.Naleznosc ? "należność" : "zobowiązanie", r.Reference, r.Account, r.DocumentDate, r.DueDate, r.Amount, r.RemainingPln, r.DaysOverdue);
        OpenItems = t;
    }

    [RelayCommand]
    private Task CompensateAsync() => ChangeAsync(async () =>
    {
        if (SelectedOpenItem?.Tag is not (OpenItemSide side, Guid receivable) || side != OpenItemSide.Naleznosc)
            throw new DomainException("Zaznacz należność na liście rozrachunków kontrahenta.");
        var payable = OpenItems.Rows.Select(r => r.Tag).OfType<(OpenItemSide, Guid)>().FirstOrDefault(x => x.Item1 == OpenItemSide.Zobowiazanie);
        if (payable == default) throw new DomainException("Kontrahent nie ma nierozliczonych zobowiązań do kompensaty.");
        if (string.IsNullOrWhiteSpace(CompensationNote)) throw new DomainException("Podaj numer noty kompensacyjnej (potwierdzenia kompensaty).");
        await using var db = Session.OpenDb();
        await new SettlementService(db, Session.User, Session.Clock).CompensateAsync(receivable, payable.Item2, Parse.Amount(CompensationAmount, "Kwota kompensaty"), Session.Clock.Today, CompensationNote.Trim());
        CompensationAmount = CompensationNote = "";
    }, "Zaksięgowano kompensatę.");

    partial void OnShowInactiveChanged(bool value) => _ = LoadAsync();

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var q = db.Counterparties.AsNoTracking().Where(c => c.CompanyId == Session.CompanyId);
        if (!ShowInactive) q = q.Where(c => c.IsActive);
        var list = await q.OrderBy(c => c.Code).ToListAsync();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            list = list.Where(c => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || c.Code.Contains(s, StringComparison.OrdinalIgnoreCase) || (c.Nip ?? "").Contains(s)).ToList();
        }
        var t = new TableData(new TableColumn("Kod", Width: 0.6), new TableColumn("Nazwa", Width: 2.5), new TableColumn("NIP", Width: 1), new TableColumn("Kraj", Width: 0.4),
            new TableColumn("Adres", Width: 2.2), new TableColumn("Rodzaj", Width: 1), new TableColumn("Termin (dni)", ColumnKind.Integer, 0.6), new TableColumn("Aktywny", Width: 0.5));
        foreach (var c in list) t.Add(c.Id, c.Code, c.Name, c.Nip ?? c.VatEuNumber, c.CountryCode, c.AddressLine, KindName(c.Kind), c.DefaultPaymentDays, c.IsActive);
        List = t;
    }

    [RelayCommand] private Task SearchNowAsync() => LoadAsync();

    [RelayCommand]
    private async Task NewAsync()
    {
        _editedId = null;
        FormHeader = "Nowy kontrahent";
        await using var db = Session.OpenDb();
        var count = await db.Counterparties.CountAsync(c => c.CompanyId == Session.CompanyId);
        Code = (count + 1).ToString("0000");
        Name = Nip = VatEu = Regon = Street = Building = Apartment = PostalCode = City = Email = Phone = Iban = Notes = "";
        Country = "PL"; PaymentDays = "14"; RelatedParty = false; Active = true; Kind = Kinds[0];
    }

    partial void OnSelectedChanged(TableRow? value)
    {
        if (value?.Tag is not Guid id) return;
        _ = RunAsync(async () =>
        {
            await using var db = Session.OpenDb();
            var c = await db.Counterparties.AsNoTracking().FirstAsync(x => x.Id == id);
            _editedId = c.Id;
            FormHeader = $"Edycja: {c.Code} {c.Name}";
            Code = c.Code; Name = c.Name; Kind = Kinds.First(k => k.Value == c.Kind); Nip = c.Nip ?? ""; VatEu = c.VatEuNumber ?? ""; Country = c.CountryCode;
            Regon = c.Regon ?? ""; Street = c.Street ?? ""; Building = c.BuildingNumber ?? ""; Apartment = c.ApartmentNumber ?? ""; PostalCode = c.PostalCode ?? "";
            City = c.City ?? ""; Email = c.Email ?? ""; Phone = c.Phone ?? ""; Iban = c.BankAccountIban ?? ""; PaymentDays = c.DefaultPaymentDays.ToString();
            RelatedParty = c.IsRelatedParty; Active = c.IsActive; Notes = c.Notes ?? "";
            await LoadOpenItemsAsync(c.Id);
        });
    }

    [RelayCommand]
    private Task SaveAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var c = _editedId is { } id ? await db.Counterparties.FirstAsync(x => x.Id == id) : new Counterparty { CompanyId = Session.CompanyId, CreatedAtUtc = Session.Clock.UtcNow };
        c.Code = Code.Trim(); c.Name = Name.Trim(); c.Kind = Kind.Value;
        c.CountryCode = Country.Trim().ToUpperInvariant();
        c.Nip = Parse.Trim(Nip) is { } n && c.CountryCode == "PL" ? Identifiers.NormalizeNip(n) : Parse.Trim(Nip);
        c.VatEuNumber = Parse.Trim(VatEu); c.Regon = Parse.Trim(Regon);
        c.Street = Parse.Trim(Street); c.BuildingNumber = Parse.Trim(Building); c.ApartmentNumber = Parse.Trim(Apartment); c.PostalCode = Parse.Trim(PostalCode); c.City = Parse.Trim(City);
        c.Email = Parse.Trim(Email); c.Phone = Parse.Trim(Phone); c.BankAccountIban = Parse.Trim(Iban)?.Replace(" ", "");
        c.DefaultPaymentDays = Parse.Int(PaymentDays, "Termin płatności"); c.IsRelatedParty = RelatedParty; c.IsActive = Active; c.Notes = Parse.Trim(Notes);
        var v = c.Validate();
        v.ThrowIfInvalid();
        if (await db.Counterparties.AnyAsync(x => x.CompanyId == Session.CompanyId && x.Code == c.Code && x.Id != c.Id))
            throw new DomainException($"Kod {c.Code} jest już używany.");
        if (_editedId is null) db.Counterparties.Add(c);
        await new AuditService(db, Session.Clock).AddAsync(Session.User, _editedId is null ? "KONTRAHENT_DODANY" : "KONTRAHENT_ZMIENIONY", "Counterparty", c.Id.ToString(), $"{c.Code} {c.Name}");
        await db.SaveChangesAsync();
        _editedId = c.Id;
        if (v.Warnings.Count > 0) await Dialogs.InfoAsync("Kontrahent zapisany", string.Join(Environment.NewLine, v.Warnings));
    }, "Zapisano kontrahenta.");
}
