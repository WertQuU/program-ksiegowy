using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Dane jednostki, osoby odpowiedzialne, polityka rachunkowości i lata obrotowe.</summary>
public sealed partial class CompanyViewModel : SectionViewModel
{
    public CompanyViewModel(AppSession s, IDialogService d) : base(s, d, "Jednostka i polityka", "🏢") { }

    public CompanyForm Form { get; } = new();

    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private TableData _persons = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedPerson;
    [ObservableProperty] private string _personFirstName = "";
    [ObservableProperty] private string _personLastName = "";
    [ObservableProperty] private Choice<ResponsibleRole> _personRole = Choices.Of<ResponsibleRole>()[0];
    [ObservableProperty] private bool _personSigns = true;
    public List<Choice<ResponsibleRole>> PersonRoles { get; } = Choices.Of<ResponsibleRole>();

    [ObservableProperty] private string _policyContent = "";
    [ObservableProperty] private string _policyInfo = "";
    [ObservableProperty] private Choice<CorrectionMethod> _correctionMethod = Choices.Of<CorrectionMethod>()[0];
    public List<Choice<CorrectionMethod>> CorrectionMethods { get; } = Choices.Of<CorrectionMethod>();
    [ObservableProperty] private string _lowValueThreshold = "10 000,00";
    [ObservableProperty] private bool _isSmallEntity;
    [ObservableProperty] private bool _isMicroEntity;
    private Guid? _policyId;

    [ObservableProperty] private TableData _years = TableData.Empty;

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == Session.CompanyId);
        Form.Load(company);
        ValidationText = company.Validate().ToString();

        var persons = new TableData(new TableColumn("Imię i nazwisko", Width: 2), new TableColumn("Funkcja", Width: 2), new TableColumn("Podpisuje sprawozdanie"));
        foreach (var p in await db.ResponsiblePersons.AsNoTracking().Where(p => p.CompanyId == company.Id).OrderBy(p => p.LastName).ToListAsync())
            persons.Add(p.Id, p.FullName, Display.Enum(p.Role), p.SignsFinancialStatements);
        Persons = persons;

        var policy = await db.AccountingPolicies.AsNoTracking().Where(p => p.CompanyId == company.Id).OrderByDescending(p => p.Version).FirstOrDefaultAsync();
        _policyId = policy?.Id;
        PolicyContent = policy?.Content ?? "";
        if (policy is not null)
        {
            CorrectionMethod = CorrectionMethods.First(c => c.Value == policy.CorrectionMethod);
            LowValueThreshold = Money.Format(policy.LowValueAssetThreshold);
            IsSmallEntity = policy.IsSmallEntity;
            IsMicroEntity = policy.IsMicroEntity;
            PolicyInfo = $"Wersja {policy.Version}, obowiązuje od {policy.EffectiveFrom.ToPl()}. " +
                         (policy.ApprovedByManagement ? $"Zatwierdzona {policy.ApprovedOn.ToPl()} przez: {policy.ApprovedBy}." : "NIEZATWIERDZONA — księgowanie jest zablokowane do czasu zatwierdzenia.");
        }
        else PolicyInfo = "Brak polityki rachunkowości.";

        var years = new TableData(new TableColumn("Rok"), new TableColumn("Od", ColumnKind.Date), new TableColumn("Do", ColumnKind.Date),
            new TableColumn("Status"), new TableColumn("Pierwszy rok"), new TableColumn("BO zatwierdzony"));
        foreach (var y in Session.Years) years.Add(y.Id, y.Name, y.StartDate, y.EndDate, y.Status, y.IsFirstYear, y.OpeningBalanceApproved);
        Years = years;
    }

    [RelayCommand]
    private Task SaveCompanyAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var existing = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == Session.CompanyId);
        await new SetupService(db, Session.Clock).UpdateCompanyAsync(Session.User, Form.ToCompany(existing));
        await Session.RefreshAsync();
    }, "Zapisano dane jednostki.");

    [RelayCommand]
    private Task AddPersonAsync() => ChangeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(PersonFirstName) || string.IsNullOrWhiteSpace(PersonLastName)) throw new DomainException("Podaj imię i nazwisko.");
        await using var db = Session.OpenDb();
        var p = new ResponsiblePerson
        {
            CompanyId = Session.CompanyId, FirstName = PersonFirstName.Trim(), LastName = PersonLastName.Trim(), Role = PersonRole.Value,
            SignsFinancialStatements = PersonSigns, From = Session.Clock.Today
        };
        db.ResponsiblePersons.Add(p);
        await new AuditService(db, Session.Clock).AddAsync(Session.User, "OSOBA_ODPOWIEDZIALNA_DODANA", "ResponsiblePerson", p.Id.ToString(), $"{p.FullName}; {p.Role}");
        await db.SaveChangesAsync();
        PersonFirstName = PersonLastName = "";
    }, "Dodano osobę odpowiedzialną.");

    [RelayCommand]
    private Task RemovePersonAsync() => ChangeAsync(async () =>
    {
        if (SelectedPerson is null) throw new DomainException("Zaznacz osobę na liście.");
        if (!await Dialogs.ConfirmAsync("Osoby odpowiedzialne", $"Usunąć „{SelectedPerson.Cells[0]}” z listy osób odpowiedzialnych?")) return;
        await using var db = Session.OpenDb();
        var p = await db.ResponsiblePersons.FirstAsync(x => x.Id == SelectedPerson.Id);
        db.ResponsiblePersons.Remove(p);
        await new AuditService(db, Session.Clock).AddAsync(Session.User, "OSOBA_ODPOWIEDZIALNA_USUNIETA", "ResponsiblePerson", p.Id.ToString(), p.FullName);
        await db.SaveChangesAsync();
    }, "Usunięto osobę odpowiedzialną.");

    [RelayCommand]
    private Task SavePolicyAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var setup = new SetupService(db, Session.Clock);
        var id = _policyId ?? (await setup.CreatePolicyFromTemplateAsync(Session.User, Session.CompanyId)).Id;
        await setup.SavePolicyAsync(Session.User, id, PolicyContent, CorrectionMethod.Value, Parse.Amount(LowValueThreshold, "Próg niskiej wartości"), IsSmallEntity, IsMicroEntity);
    }, "Zapisano politykę rachunkowości (zmiana zatwierdzonej polityki tworzy nową, niezatwierdzoną wersję).");

    [RelayCommand]
    private Task NewPolicyFromTemplateAsync() => ChangeAsync(async () =>
    {
        if (_policyId is not null && !await Dialogs.ConfirmAsync("Polityka rachunkowości", "Utworzyć nową wersję polityki z szablonu? Bieżąca wersja pozostanie w historii.")) return;
        await using var db = Session.OpenDb();
        await new SetupService(db, Session.Clock).CreatePolicyFromTemplateAsync(Session.User, Session.CompanyId);
    }, "Utworzono politykę z szablonu — uzupełnij pola oznaczone [DO UZUPEŁNIENIA].");

    [RelayCommand]
    private Task ApprovePolicyAsync() => ChangeAsync(async () =>
    {
        if (_policyId is null) throw new DomainException("Brak polityki do zatwierdzenia.");
        var who = await Dialogs.PromptAsync("Zatwierdzenie polityki rachunkowości",
            $"Politykę zatwierdza kierownik jednostki ({LegalForms.ManagementBody(Form.LegalForm.Value)}). Podaj imię, nazwisko i funkcję osoby zatwierdzającej:");
        if (string.IsNullOrWhiteSpace(who)) return;
        await using var db = Session.OpenDb();
        await new SetupService(db, Session.Clock).ApprovePolicyAsync(Session.User, _policyId.Value, who.Trim());
    }, "Polityka rachunkowości zatwierdzona.");

    [RelayCommand]
    private Task AddNextYearAsync() => ChangeAsync(async () =>
    {
        var last = Session.Years.LastOrDefault() ?? throw new DomainException("Brak roku obrotowego.");
        var start = last.EndDate.AddDays(1);
        var end = start.AddYears(1).AddDays(-1);
        if (!await Dialogs.ConfirmAsync("Nowy rok obrotowy", $"Utworzyć rok obrotowy {start.ToPl()} – {end.ToPl()} z kopią planu kont roku {last.Name}?" +
                Environment.NewLine + "Bilans otwarcia powstanie automatycznie przy zamknięciu roku poprzedniego.")) return;
        await using var db = Session.OpenDb();
        var fy = await new SetupService(db, Session.Clock).CreateFiscalYearAsync(Session.User, Session.CompanyId, start, end, false);
        var chart = await db.Charts.Where(c => c.FiscalYearId == last.Id).OrderByDescending(c => c.Version).FirstOrDefaultAsync();
        if (chart is not null) await new ChartService(db, Session.User, Session.Clock).CopyChartToYearAsync(chart.Id, fy.Id);
        await Session.RefreshAsync();
    }, "Utworzono kolejny rok obrotowy.");
}
