using CommunityToolkit.Mvvm.ComponentModel;
using PelnaKsiegowosc.Domain.Companies;

namespace PelnaKsiegowosc.Presentation.ViewModels;

/// <summary>Formularz danych jednostki — wspólny dla kreatora i sekcji „Jednostka i polityka”.</summary>
public sealed partial class CompanyForm : ObservableObject
{
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _shortName = "";
    [ObservableProperty] private string _nip = "";
    [ObservableProperty] private string? _regon;
    [ObservableProperty] private string? _krs;
    [ObservableProperty] private string? _registryCourt;
    [ObservableProperty] private string _street = "";
    [ObservableProperty] private string _buildingNumber = "";
    [ObservableProperty] private string? _apartmentNumber;
    [ObservableProperty] private string _postalCode = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string? _voivodeship;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _phone;
    [ObservableProperty] private string? _taxOfficeCode;
    [ObservableProperty] private string? _bankIban;
    [ObservableProperty] private string? _invoiceFooter;
    [ObservableProperty] private string? _registrationDate;
    [ObservableProperty] private bool _isSmallCitTaxpayer;
    [ObservableProperty] private Choice<LegalForm> _legalForm = LegalForms_[0];
    [ObservableProperty] private Choice<VatStatus> _vatStatus = Choices.Of<VatStatus>()[0];
    [ObservableProperty] private Choice<VatSettlementPeriod> _vatPeriod = Choices.Of<VatSettlementPeriod>()[0];
    [ObservableProperty] private Choice<CitAdvanceMethod> _citMethod = Choices.Of<CitAdvanceMethod>()[0];

    private static readonly List<Choice<LegalForm>> LegalForms_ =
        Enum.GetValues<LegalForm>().Select(f => new Choice<LegalForm>(f, LegalForms.Name(f))).ToList();

    public List<Choice<LegalForm>> LegalFormChoices => LegalForms_;

    public List<Choice<VatStatus>> VatStatuses { get; } = Choices.Of<VatStatus>();
    public List<Choice<VatSettlementPeriod>> VatPeriods { get; } = Choices.Of<VatSettlementPeriod>();
    public List<Choice<CitAdvanceMethod>> CitMethods { get; } = Choices.Of<CitAdvanceMethod>();

    public void Load(Company c)
    {
        FullName = c.FullName; ShortName = c.ShortName; Nip = c.Nip; Regon = c.Regon; Krs = c.Krs; RegistryCourt = c.RegistryCourt;
        Street = c.Street; BuildingNumber = c.BuildingNumber; ApartmentNumber = c.ApartmentNumber; PostalCode = c.PostalCode; City = c.City;
        Voivodeship = c.Voivodeship; Email = c.Email; Phone = c.Phone; TaxOfficeCode = c.TaxOfficeCode; BankIban = c.DefaultBankAccountIban;
        InvoiceFooter = c.InvoiceFooter; RegistrationDate = c.RegistrationDate?.ToString("dd.MM.yyyy"); IsSmallCitTaxpayer = c.IsSmallCitTaxpayer;
        LegalForm = LegalFormChoices.First(x => x.Value == c.LegalForm);
        VatStatus = VatStatuses.First(x => x.Value == c.VatStatus);
        VatPeriod = VatPeriods.First(x => x.Value == c.VatPeriod);
        CitMethod = CitMethods.First(x => x.Value == c.CitAdvanceMethod);
    }

    /// <summary>Przenosi dane formularza na encję (nowa albo kopia istniejącej z zachowanym identyfikatorem).</summary>
    public Company ToCompany(Company? existing = null)
    {
        var c = new Company();
        if (existing is not null)
        {
            c.Id = existing.Id;
            c.CreatedAtUtc = existing.CreatedAtUtc;
            c.FunctionalCurrency = existing.FunctionalCurrency;
            c.Country = existing.Country;
        }
        c.LegalForm = LegalForm.Value;
        c.FullName = FullName.Trim();
        c.ShortName = string.IsNullOrWhiteSpace(ShortName) ? FullName.Trim() : ShortName.Trim();
        c.Nip = Nip.Trim();
        c.Regon = Parse.Trim(Regon); c.Krs = Parse.Trim(Krs); c.RegistryCourt = Parse.Trim(RegistryCourt);
        c.Street = Street.Trim(); c.BuildingNumber = BuildingNumber.Trim(); c.ApartmentNumber = Parse.Trim(ApartmentNumber);
        c.PostalCode = PostalCode.Trim(); c.City = City.Trim(); c.Voivodeship = Parse.Trim(Voivodeship);
        c.Email = Parse.Trim(Email); c.Phone = Parse.Trim(Phone); c.TaxOfficeCode = Parse.Trim(TaxOfficeCode);
        c.DefaultBankAccountIban = Parse.Trim(BankIban)?.Replace(" ", ""); c.InvoiceFooter = Parse.Trim(InvoiceFooter);
        c.RegistrationDate = Parse.OptionalDate(RegistrationDate, "Data rejestracji w KRS");
        c.IsSmallCitTaxpayer = IsSmallCitTaxpayer;
        c.VatStatus = VatStatus.Value; c.VatPeriod = VatPeriod.Value; c.CitAdvanceMethod = CitMethod.Value;
        return c;
    }
}
