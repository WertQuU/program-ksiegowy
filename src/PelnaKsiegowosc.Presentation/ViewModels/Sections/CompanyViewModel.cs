using PelnaKsiegowosc.Application.Security;
using PelnaKsiegowosc.Domain.Security;
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

    // operatorzy i uprawnienia (rozdział obowiązków; nadaje administrator)
    [ObservableProperty] private TableData _operators = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedOperator;
    [ObservableProperty] private string _operatorsInfo = "";
    [ObservableProperty] private string _newOperatorLogin = "";
    [ObservableProperty] private bool _opAdmin;
    [ObservableProperty] private bool _opChief;
    [ObservableProperty] private bool _opAccountant;
    [ObservableProperty] private bool _opInvoicing;
    [ObservableProperty] private bool _opBoard;
    [ObservableProperty] private bool _opAuditor;
    [ObservableProperty] private bool _opActive = true;

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

        var me = $"Zalogowany operator: {Session.User.Login} — role: {(Session.User.Roles.Count == 0 ? "brak (poproś administratora o nadanie uprawnień)" : string.Join(", ", Session.User.Roles.Select(RolePermissions.Name)))}.";
        if (Session.User.Can(Permission.ManageUsers))
        {
            var ops = new TableData(new TableColumn("Login PostgreSQL", Width: 2), new TableColumn("Role", Width: 3), new TableColumn("Aktywny", Width: 0.6),
                new TableColumn("Ostatnio", ColumnKind.Date));
            foreach (var o in await new OperatorAccessService(db, Session.Clock).ListAsync(Session.User))
                ops.Add(o.Id, o.Login, string.Join(", ", o.Roles.Select(RolePermissions.Name)), o.IsActive, o.LastSeenUtc is { } ls ? DateOnly.FromDateTime(ls) : (DateOnly?)null);
            Operators = ops;
            OperatorsInfo = me + " Administrator najpierw tworzy osobne konto LOGIN w PostgreSQL i nadaje mu ograniczone prawa SQL; tutaj rejestruje db:login i role aplikacji. Nieznane loginy są odrzucane. Jeden login może pracować na różnych urządzeniach, ale tylko w jednej aktywnej sesji programu naraz.";
        }
        else
        {
            Operators = TableData.Empty;
            OperatorsInfo = me + " Uprawnieniami zarządza administrator.";
        }
    }

    partial void OnSelectedOperatorChanged(TableRow? value)
    {
        if (value is null) return;
        var roles = (value.Cells[1] ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        bool H(Role r) => roles.Contains(RolePermissions.Name(r));
        OpAdmin = H(Role.Administrator); OpChief = H(Role.GlownyKsiegowy); OpAccountant = H(Role.Ksiegowy);
        OpInvoicing = H(Role.Fakturzysta); OpBoard = H(Role.Zarzad); OpAuditor = H(Role.Audytor);
        OpActive = value.Cells[2] == "tak";
    }

    private List<Role> SelectedOperatorRoles()
    {
        var roles = new List<Role>();
        if (OpAdmin) roles.Add(Role.Administrator);
        if (OpChief) roles.Add(Role.GlownyKsiegowy);
        if (OpAccountant) roles.Add(Role.Ksiegowy);
        if (OpInvoicing) roles.Add(Role.Fakturzysta);
        if (OpBoard) roles.Add(Role.Zarzad);
        if (OpAuditor) roles.Add(Role.Audytor);
        return roles;
    }

    [RelayCommand]
    private Task AddOperatorAsync() => ChangeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewOperatorLogin)) throw new DomainException("Podaj db:login użytkownika PostgreSQL.");
        await using var db = Session.OpenDb();
        await new OperatorAccessService(db, Session.Clock).RegisterAsync(Session.User, NewOperatorLogin, SelectedOperatorRoles());
        NewOperatorLogin = "";
    }, "Dodano operatora/stanowisko; uprawnienia zapisano w dzienniku zdarzeń.");

    [RelayCommand]
    private Task SaveOperatorAsync() => ChangeAsync(async () =>
    {
        if (SelectedOperator?.Tag is not Guid id) throw new DomainException("Zaznacz operatora.");
        await using var db = Session.OpenDb();
        await new OperatorAccessService(db, Session.Clock).SetAsync(Session.User, id, SelectedOperatorRoles(), OpActive);
    }, "Zapisano uprawnienia operatora (zmiana zapisana w dzienniku zdarzeń).");

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
