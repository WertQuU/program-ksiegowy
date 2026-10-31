using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Infrastructure.Export;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Kadry i płace: osoby i umowy, listy płac (wyliczenie ZUS/PIT, księgowanie), zestawienia ZUS DRA/RCA, PIT-4R, PIT-8AR, PIT-11.</summary>
public sealed partial class PayrollViewModel : SectionViewModel
{
    public PayrollViewModel(AppSession s, IDialogService d) : base(s, d, "Płace", "👥")
    {
        _contract = ContractTypes[0];
        _costs = CostTypes[0];
        _pit2Option = Pit2Options[1];
        _externalTitles = ExternalTitleOptions[0];
    }

    public List<Choice<ContractType>> ContractTypes { get; } = new()
    {
        new(ContractType.UmowaOPrace, "Umowa o pracę"), new(ContractType.UmowaZlecenie, "Umowa zlecenia"),
        new(ContractType.UmowaODzielo, "Umowa o dzieło"), new(ContractType.Powolanie, "Powołanie (np. członek zarządu)")
    };
    public List<Choice<CostsOfIncome>> CostTypes { get; } = new()
    {
        new(CostsOfIncome.Standardowe, "Standardowe (250 zł / 20%)"), new(CostsOfIncome.Podwyzszone, "Podwyższone 300 zł (dojazd)"),
        new(CostsOfIncome.Autorskie50, "Autorskie 50%"), new(CostsOfIncome.Brak, "Bez kosztów")
    };

    public List<Choice<decimal>> Pit2Options { get; } = new()
    {
        new(0m, "PIT-2: brak"), new(300m, "PIT-2: 1/12 — 300 zł"), new(150m, "PIT-2: 1/24 — 150 zł"), new(100m, "PIT-2: 1/36 — 100 zł")
    };
    public List<Choice<ExternalInsuranceTitle>> ExternalTitleOptions { get; } = new()
    {
        new(ExternalInsuranceTitle.Nieustalone, "Zbieg tytułów: nieustalony"), new(ExternalInsuranceTitle.Brak, "Brak innych tytułów"),
        new(ExternalInsuranceTitle.EtatUInnegoPracodawcy, "Etat u innego pracodawcy"), new(ExternalInsuranceTitle.ZlecenieUInnegoZleceniodawcy, "Wcześniejsze zlecenie u innego zleceniodawcy"),
        new(ExternalInsuranceTitle.ObowiazkoweWgUstalenia, "Inny zbieg — zlecenie obowiązkowe (ustalone)"), new(ExternalInsuranceTitle.DobrowolneWgUstalenia, "Inny zbieg — zlecenie nieobowiązkowe (ustalone)")
    };
    [ObservableProperty] private Choice<decimal>? _pit2Option;
    [ObservableProperty] private Choice<ExternalInsuranceTitle>? _externalTitles;
    [ObservableProperty] private string _externalTitlesBasis = "";
    [ObservableProperty] private bool _voluntaryPension;
    [ObservableProperty] private string _ppkTransferDate = "";
    [ObservableProperty] private TableData _employees = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedEmployee;
    [ObservableProperty] private string _firstName = "";
    [ObservableProperty] private string _lastName = "";
    [ObservableProperty] private string _pesel = "";
    [ObservableProperty] private string _street = "";
    [ObservableProperty] private string _houseNumber = "";
    [ObservableProperty] private string _flatNumber = "";
    [ObservableProperty] private string _postalCode = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _voivodeship = "";
    [ObservableProperty] private string _county = "";
    [ObservableProperty] private string _commune = "";
    [ObservableProperty] private string _annualLeaveDays = "26";
    [ObservableProperty] private string _leaveCarriedOver = "";
    [ObservableProperty] private string _medicalExamUntil = "";
    [ObservableProperty] private string _bhpTrainingUntil = "";
    public List<Choice<AbsenceKind>> AbsenceKinds { get; } = new()
    {
        new(Domain.Payroll.AbsenceKind.UrlopWypoczynkowy, "Urlop wypoczynkowy"), new(Domain.Payroll.AbsenceKind.UrlopNaZadanie, "Urlop na żądanie"),
        new(Domain.Payroll.AbsenceKind.Choroba, "Choroba (L4)"), new(Domain.Payroll.AbsenceKind.UrlopBezplatny, "Urlop bezpłatny"),
        new(Domain.Payroll.AbsenceKind.Opieka, "Opieka (zasiłek ZUS)"), new(Domain.Payroll.AbsenceKind.UrlopOkolicznosciowy, "Urlop okolicznościowy"),
        new(Domain.Payroll.AbsenceKind.NieobecnoscNieusprawiedliwiona, "Nieobecność nieusprawiedliwiona"),
    };
    [ObservableProperty] private List<Choice<Guid>> _employeeChoices = new();
    [ObservableProperty] private Choice<Guid>? _absenceEmployee;
    [ObservableProperty] private Choice<AbsenceKind>? _absenceKind;
    [ObservableProperty] private string _absenceFrom = "";
    [ObservableProperty] private string _absenceTo = "";
    [ObservableProperty] private string _absenceNotes = "";
    [ObservableProperty] private TableData _absences = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedAbsence;
    [ObservableProperty] private TableData _leaveBalances = TableData.Empty;
    [ObservableProperty] private string _hrReminders = "";
    [ObservableProperty] private string _iban = "";
    [ObservableProperty] private string _taxOffice = "";
    [ObservableProperty] private Choice<ContractType> _contract;
    [ObservableProperty] private Choice<CostsOfIncome> _costs;
    [ObservableProperty] private string _gross = "";
    [ObservableProperty] private string _contractTotal = "";
    [ObservableProperty] private bool _workForOwnEmployer;
    [ObservableProperty] private string _authorShare = "100";
    [ObservableProperty] private string _authorBasis = "";
    [ObservableProperty] private string _fraction = "1";
    [ObservableProperty] private string _startDate = "";
    [ObservableProperty] private string _endDate = "";
    [ObservableProperty] private bool _youthRelief;
    [ObservableProperty] private bool _student;
    [ObservableProperty] private bool _voluntarySickness;
    [ObservableProperty] private bool _exemptFromFp;
    [ObservableProperty] private bool _ppk;
    [ObservableProperty] private string _sickDays = "";
    [ObservableProperty] private string _unpaidDays = "";

    [ObservableProperty] private TableData _lists = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedList;
    [ObservableProperty] private TableData _items = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedItem;
    [ObservableProperty] private string _listMonth = "";
    [ObservableProperty] private string _paymentDate = "";
    [ObservableProperty] private string _newGross = "";
    [ObservableProperty] private string _accidentRate = "";
    [ObservableProperty] private string _reportYear = "";
    [ObservableProperty] private string _zusMonth = "";
    [ObservableProperty] private TableData _report = TableData.Empty;
    private ReportDocument? _currentReport;

    partial void OnSelectedEmployeeChanged(TableRow? value)
    {
        if (value?.Tag is not Guid id) return;
        _ = RunAsync(async () =>
        {
            await using var db = Session.OpenDb();
            var e = await db.Employees.AsNoTracking().FirstAsync(x => x.Id == id);
            FirstName = e.FirstName; LastName = e.LastName; Pesel = e.Pesel ?? ""; Street = e.Street ?? ""; HouseNumber = e.HouseNumber ?? ""; FlatNumber = e.FlatNumber ?? ""; PostalCode = e.PostalCode ?? ""; City = e.City ?? ""; Voivodeship = e.Voivodeship ?? ""; County = e.County ?? ""; Commune = e.Commune ?? "";
            AnnualLeaveDays = e.AnnualLeaveDays.ToString(); LeaveCarriedOver = e.LeaveCarriedOver == 0 ? "" : e.LeaveCarriedOver.ToString("0.##", Money.Pl);
            MedicalExamUntil = e.MedicalExamUntil?.ToPl() ?? ""; BhpTrainingUntil = e.BhpTrainingUntil?.ToPl() ?? ""; Iban = e.BankAccountIban ?? ""; TaxOffice = e.TaxOfficeCode ?? "";
            Contract = ContractTypes.First(c => c.Value == e.ContractType); Costs = CostTypes.First(c => c.Value == e.Costs);
            Gross = e.Gross.ToString("N2", Money.Pl); ContractTotal = e.ContractTotalAmount?.ToString("N2", Money.Pl) ?? ""; Fraction = e.WorkTimeFraction.ToString("0.####", Money.Pl);
            StartDate = e.StartDate.ToPl(); EndDate = e.EndDate?.ToPl() ?? "";
            Pit2Option = Pit2Options.FirstOrDefault(o => o.Value == e.Pit2Reduction) ?? Pit2Options[1];
            ExternalTitles = ExternalTitleOptions.First(o => o.Value == e.ExternalTitles); ExternalTitlesBasis = e.ExternalTitlesBasis?.ToString("N2", Money.Pl) ?? ""; VoluntaryPension = e.VoluntaryPension;
            YouthRelief = e.YouthRelief; Student = e.StudentUnder26; VoluntarySickness = e.VoluntarySickness; ExemptFromFp = e.ExemptFromFp; Ppk = e.Ppk; WorkForOwnEmployer = e.WorkForOwnEmployer;
            AuthorShare = e.AuthorSharePercent.ToString("0.##", Money.Pl); AuthorBasis = e.AuthorCostsBasis ?? "";
        });
    }

    partial void OnSelectedListChanged(TableRow? value)
    {
        if (value?.Tag is Guid) _ = RunAsync(LoadItemsAsync);
    }

    private async Task LoadItemsAsync()
    {
        if (SelectedList?.Tag is not Guid id) { Items = TableData.Empty; return; }
        await using var db = Session.OpenDb();
        var list = await db.PayrollLists.AsNoTracking().Include(l => l.Items).FirstAsync(l => l.Id == id);
        var t = new TableData(new TableColumn("Osoba", Width: 1.8), new TableColumn("Umowa", Width: 1.1), new TableColumn("Brutto bazowe", ColumnKind.Money), new TableColumn("Dni chor./bezpł.", Width: 0.7), new TableColumn("Brutto", ColumnKind.Money), new TableColumn("ZUS prac.", ColumnKind.Money),
            new TableColumn("Zdrowotna", ColumnKind.Money), new TableColumn("Zaliczka PIT", ColumnKind.Money), new TableColumn("Netto", ColumnKind.Money),
            new TableColumn("Koszt pracodawcy", ColumnKind.Money), new TableColumn("Uwagi", Width: 2.2));
        foreach (var i in list.Items.OrderBy(i => i.EmployeeName))
            t.Add(i.Id, i.EmployeeName, ContractTypes.First(c => c.Value == i.ContractType).Label, i.BaseGross, $"{i.SickDays} / {i.UnpaidDays}", i.Gross, i.SocialEmployee, i.Health, i.TaxAdvance, i.Net, i.EmployerCost, i.Notes);
        Items = t;
    }

    protected override async Task LoadCoreAsync()
    {
        var today = Session.Clock.Today;
        if (string.IsNullOrEmpty(ListMonth)) ListMonth = $"{today.Month:00}.{today.Year}";
        if (string.IsNullOrEmpty(PaymentDate)) PaymentDate = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1).ToPl();
        if (string.IsNullOrEmpty(ReportYear)) ReportYear = today.Year.ToString();
        if (string.IsNullOrEmpty(ZusMonth)) ZusMonth = ListMonth;
        if (string.IsNullOrEmpty(StartDate)) StartDate = today.ToPl();
        await using var db = Session.OpenDb();
        AccidentRate = (await new PayrollService(db, Session.User, Session.Clock).AccidentRateAsync()).ToString("0.00", Money.Pl);
        var e = new TableData(new TableColumn("Imię i nazwisko", Width: 1.8), new TableColumn("Umowa", Width: 1.2), new TableColumn("Brutto", ColumnKind.Money),
            new TableColumn("Od", ColumnKind.Date), new TableColumn("Do", ColumnKind.Date), new TableColumn("PIT-2"), new TableColumn("Aktywna"));
        foreach (var x in await db.Employees.AsNoTracking().Where(x => x.CompanyId == Session.CompanyId).OrderBy(x => x.LastName).ToListAsync())
            e.Add(x.Id, x.FullName, ContractTypes.First(c => c.Value == x.ContractType).Label, x.Gross, x.StartDate, x.EndDate, x.Pit2Reduction > 0, x.IsActive);
        Employees = e;
        EmployeeChoices = (await db.Employees.AsNoTracking().Where(x => x.CompanyId == Session.CompanyId && x.IsActive).OrderBy(x => x.LastName).ToListAsync())
            .Select(x => new Choice<Guid>(x.Id, x.FullName)).ToList();
        AbsenceKind ??= AbsenceKinds[0];
        await LoadHrAsync(db);
        var l = new TableData(new TableColumn("Numer"), new TableColumn("Za miesiąc"), new TableColumn("Wypłata", ColumnKind.Date), new TableColumn("Status"),
            new TableColumn("Brutto", ColumnKind.Money), new TableColumn("Netto", ColumnKind.Money));
        foreach (var x in await db.PayrollLists.AsNoTracking().Include(x => x.Items).Where(x => x.CompanyId == Session.CompanyId).OrderByDescending(x => x.PaymentDate).ToListAsync())
            l.Add(x.Id, x.Number, x.Month.ToString("MM.yyyy"), x.PaymentDate, x.Status == PayrollStatus.Zaksiegowana ? "zaksięgowana" : "robocza", x.Items.Sum(i => i.Gross), x.Items.Sum(i => i.Net));
        Lists = l;
        await LoadItemsAsync();
    }

    private Employee FromForm(Guid? id) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        FirstName = FirstName.Trim(), LastName = LastName.Trim(), Pesel = Parse.Trim(Pesel), Street = Parse.Trim(Street), HouseNumber = Parse.Trim(HouseNumber), FlatNumber = Parse.Trim(FlatNumber), PostalCode = Parse.Trim(PostalCode), City = Parse.Trim(City),
        Voivodeship = Parse.Trim(Voivodeship), County = Parse.Trim(County), Commune = Parse.Trim(Commune),
        AnnualLeaveDays = string.IsNullOrWhiteSpace(AnnualLeaveDays) ? 26 : Parse.Int(AnnualLeaveDays, "Wymiar urlopu"), LeaveCarriedOver = Parse.Amount(LeaveCarriedOver, "Urlop zaległy"),
        MedicalExamUntil = Parse.OptionalDate(MedicalExamUntil, "Badania lekarskie do"), BhpTrainingUntil = Parse.OptionalDate(BhpTrainingUntil, "Szkolenie BHP do"),
        Address = Parse.Trim(string.Join(", ", new[] { $"{Street} {HouseNumber}{(string.IsNullOrWhiteSpace(FlatNumber) ? "" : "/" + FlatNumber)}".Trim(), $"{PostalCode} {City}".Trim() }.Where(x => x.Length > 0))), BankAccountIban = Parse.Trim(Iban),
        TaxOfficeCode = Parse.Trim(TaxOffice), ContractType = Contract.Value, Costs = Costs.Value, Gross = Parse.Amount(Gross, "Wynagrodzenie brutto"),
        ContractTotalAmount = Parse.OptionalAmount(ContractTotal, "Kwota z umowy"),
        WorkTimeFraction = Parse.Amount(Fraction, "Wymiar etatu"), StartDate = Parse.Date(StartDate, "Data rozpoczęcia"), EndDate = Parse.OptionalDate(EndDate, "Data zakończenia"),
        Pit2Reduction = Pit2Option?.Value ?? 0m, ExternalTitles = ExternalTitles?.Value ?? ExternalInsuranceTitle.Nieustalone,
        ExternalTitlesBasis = Parse.OptionalAmount(ExternalTitlesBasis, "Podstawa z innego tytułu"), VoluntaryPension = VoluntaryPension,
        YouthRelief = YouthRelief, StudentUnder26 = Student, VoluntarySickness = VoluntarySickness, ExemptFromFp = ExemptFromFp, Ppk = Ppk, WorkForOwnEmployer = WorkForOwnEmployer,
        AuthorSharePercent = string.IsNullOrWhiteSpace(AuthorShare) ? 100m : Parse.Amount(AuthorShare, "Udział wynagrodzenia autorskiego"), AuthorCostsBasis = Parse.Trim(AuthorBasis)
    };

    [RelayCommand]
    private Task AddEmployeeAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new PayrollService(db, Session.User, Session.Clock).AddEmployeeAsync(FromForm(null));
    }, "Dodano osobę. Rozrachunki z nią prowadzone są na koncie 230 (kontrahent założony automatycznie).");

    [RelayCommand]
    private Task SaveEmployeeAsync() => ChangeAsync(async () =>
    {
        if (SelectedEmployee?.Tag is not Guid id) throw new DomainException("Zaznacz osobę na liście.");
        await using var db = Session.OpenDb();
        var old = await db.Employees.AsNoTracking().FirstAsync(x => x.Id == id);
        var e = FromForm(id);
        e.CounterpartyId = old.CounterpartyId;
        e.CreatedAtUtc = old.CreatedAtUtc;
        e.IsActive = e.EndDate is null || e.EndDate >= Session.Clock.Today.AddMonths(-1);
        await new PayrollService(db, Session.User, Session.Clock).UpdateEmployeeAsync(e);
    }, "Zapisano zmiany.");

    [RelayCommand]
    private Task SaveAccidentRateAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new PayrollService(db, Session.User, Session.Clock).SetAccidentRateAsync(Parse.Amount(AccidentRate, "Stopa wypadkowa"));
    }, "Zapisano stopę składki wypadkowej.");

    [RelayCommand]
    private Task CreateListAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new PayrollService(db, Session.User, Session.Clock).CreateListAsync(Parse.Month(ListMonth, "Miesiąc"), Parse.Date(PaymentDate, "Data wypłaty"));
    }, "Utworzono listę płac — sprawdź kwoty i zaksięguj.");

    [RelayCommand]
    private Task ChangeGrossAsync() => ChangeAsync(async () =>
    {
        if (SelectedItem?.Tag is not Guid id) throw new DomainException("Zaznacz osobę na liście płac.");
        await using var db = Session.OpenDb();
        // puste pole — wartość pozycji bez zmian (dni nieobecności nie są zerowane przy zmianie kwoty)
        int? Days(string s, string f) => string.IsNullOrWhiteSpace(s) ? null : Parse.Int(s, f);
        await new PayrollService(db, Session.User, Session.Clock).ChangeAsync(id, Parse.OptionalAmount(NewGross, "Wynagrodzenie za pełny miesiąc"),
            Days(SickDays, "Dni choroby"), Days(UnpaidDays, "Dni nieobecności nieodpłatnej"));
        NewGross = SickDays = UnpaidDays = "";
    }, "Przeliczono pozycję listy płac.");

    [RelayCommand]
    private Task RecalculateListAsync() => ChangeAsync(async () =>
    {
        if (SelectedList?.Tag is not Guid id) throw new DomainException("Zaznacz listę płac.");
        await using var db = Session.OpenDb();
        await new PayrollService(db, Session.User, Session.Clock).RecalculateAsync(id);
    }, "Przeliczono listę według aktualnych danych osób, limitów i innych list.");

    [RelayCommand]
    private Task RecordPpkTransferAsync() => ChangeAsync(async () =>
    {
        if (SelectedList?.Tag is not Guid id) throw new DomainException("Zaznacz zaksięgowaną listę płac.");
        await using var db = Session.OpenDb();
        await new PayrollService(db, Session.User, Session.Clock).RecordPpkTransferAsync(id, Parse.Date(PpkTransferDate, "Data przekazania PPK"));
    }, "Zarejestrowano przekazanie wpłat PPK — wpłata pracodawcy zostanie opodatkowana w najbliższej liście płac.");

    [RelayCommand]
    private Task DeleteListAsync() => ChangeAsync(async () =>
    {
        if (SelectedList?.Tag is not Guid id) throw new DomainException("Zaznacz listę płac.");
        if (!await Dialogs.ConfirmAsync("Usunięcie listy płac", "Usunąć roboczą listę płac?")) return;
        await using var db = Session.OpenDb();
        await new PayrollService(db, Session.User, Session.Clock).DeleteListAsync(id);
    }, "Usunięto listę płac.");

    [RelayCommand]
    private Task PostListAsync() => ChangeAsync(async () =>
    {
        if (SelectedList?.Tag is not Guid id) throw new DomainException("Zaznacz listę płac.");
        if (!await Dialogs.ConfirmAsync("Księgowanie listy płac", "Zaksięgować listę płac? Wynagrodzenia netto trafią na konto 230 jako do wypłaty, składki — na 220-03, zaliczki PIT — na 220-02.")) return;
        await using var db = Session.OpenDb();
        await new PayrollService(db, Session.User, Session.Clock).PostAsync(id);
    }, "Lista płac zaksięgowana. Wypłaty i przelewy do ZUS/US zaksięgujesz w sekcji Bank.");

    private async Task LoadHrAsync(Data.KsiegiDbContext db)
    {
        var year = Parse.Int(string.IsNullOrWhiteSpace(ReportYear) ? Session.Clock.Today.Year.ToString() : ReportYear, "Rok");
        var hr = new HrService(db, Session.User, Session.Clock);
        var names = EmployeeChoices.ToDictionary(c => c.Value, c => c.Label);
        var a = new TableData(new TableColumn("Pracownik", Width: 1.6), new TableColumn("Rodzaj", Width: 1.3), new TableColumn("Od", ColumnKind.Date), new TableColumn("Do", ColumnKind.Date),
            new TableColumn("Dni robocze", ColumnKind.Integer, 0.6), new TableColumn("Uwagi", Width: 1.2));
        foreach (var x in await hr.AbsencesAsync(year))
            a.Add(x.Id, names.GetValueOrDefault(x.EmployeeId, "?"), AbsenceKinds.First(k => k.Value == x.Kind).Label, x.From, x.To, HrService.WorkingDays(x.From, x.To), x.Notes);
        Absences = a;
        var b = new TableData(new TableColumn("Pracownik", Width: 1.6), new TableColumn("Wymiar", ColumnKind.Money), new TableColumn("Zaległy", ColumnKind.Money),
            new TableColumn("Wykorzystany", ColumnKind.Money), new TableColumn("Pozostało", ColumnKind.Money));
        foreach (var x in await hr.LeaveBalancesAsync(year)) b.Add(x.EmployeeId, x.Employee, x.Entitlement, x.CarriedOver, x.Used, x.Remaining);
        LeaveBalances = b;
        var rem = await hr.RemindersAsync();
        HrReminders = rem.Count == 0 ? "Brak zbliżających się terminów kadrowych (badania, BHP, koniec umów)." : "Terminy: " + string.Join("; ", rem.Select(r => $"{r.Employee} — {r.What} ({r.Date.ToPl()})"));
    }

    [RelayCommand]
    private Task RefreshHrAsync() => RunAsync(async () => { await using var db = Session.OpenDb(); await LoadHrAsync(db); });

    [RelayCommand]
    private Task AddAbsenceAsync() => RunAsync(async () =>
    {
        if (AbsenceEmployee is null) throw new DomainException("Wybierz pracownika.");
        await using var db = Session.OpenDb();
        await new HrService(db, Session.User, Session.Clock).AddAbsenceAsync(AbsenceEmployee.Value, (AbsenceKind ?? AbsenceKinds[0]).Value,
            Parse.Date(AbsenceFrom, "Od"), Parse.Date(string.IsNullOrWhiteSpace(AbsenceTo) ? AbsenceFrom : AbsenceTo, "Do"), Parse.Trim(AbsenceNotes));
        AbsenceNotes = "";
        await LoadHrAsync(db);
    }, "Nieobecność zapisana.");

    [RelayCommand]
    private Task DeleteAbsenceAsync() => RunAsync(async () =>
    {
        if (SelectedAbsence?.Tag is not Guid id) throw new DomainException("Zaznacz nieobecność.");
        await using var db = Session.OpenDb();
        await new HrService(db, Session.User, Session.Clock).DeleteAbsenceAsync(id);
        await LoadHrAsync(db);
    }, "Nieobecność usunięta.");

    /// <summary>RODO art. 17 dla pracownika/zleceniobiorcy — po upływie 10 lat od końca roku zakończenia umowy.</summary>
    [RelayCommand]
    private Task EraseEmployeeDataAsync() => ChangeAsync(async () =>
    {
        if (SelectedEmployee?.Tag is not Guid id) throw new DomainException("Zaznacz osobę na liście.");
        await using var db = Session.OpenDb();
        var svc = new Application.Security.PersonalDataService(db, Session.User, Session.Clock);
        var check = await svc.EmployeeRetentionAsync(id);
        var request = await Dialogs.PromptAsync("Żądanie usunięcia danych (RODO)", check.Basis + Environment.NewLine + Environment.NewLine + "Opisz żądanie (data wpływu, kto, w jakiej formie):");
        if (string.IsNullOrWhiteSpace(request)) return;
        var r = await svc.EraseEmployeeAsync(id, request);
        await Dialogs.InfoAsync("RODO", r.CanErase ? "Dane osobowe zanonimizowano. Zdarzenie zapisano w dzienniku." : $"Odmowa usunięcia (art. 17 ust. 3 lit. b RODO): {r.Basis}");
    }, "Obsłużono żądanie RODO.");

    [RelayCommand] private Task SalaryCardAsync() => SelectedEmployee?.Tag is Guid id
        ? ShowReportAsync(r => r.SalaryCardAsync(id, Parse.Int(ReportYear, "Rok")))
        : RunAsync(() => throw new DomainException("Zaznacz osobę na liście w zakładce „Osoby i umowy”."));
    [RelayCommand] private Task LeaveReportAsync() => ShowReportAsync(r => r.LeaveReportAsync(Parse.Int(ReportYear, "Rok")));

    private Task ShowReportAsync(Func<ReportService, Task<ReportDocument>> build) => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        _currentReport = await build(new ReportService(db, Session.User, Session.Clock));
        Report = TableData.FromReport(_currentReport);
        StatusMessage = _currentReport.Title;
    });

    [RelayCommand] private Task PrintListAsync() => SelectedList?.Tag is Guid id ? ShowReportAsync(r => r.PayrollListAsync(id)) : RunAsync(() => throw new DomainException("Zaznacz listę płac."));
    [RelayCommand] private Task ZusReportAsync() => ShowReportAsync(r => r.ZusMonthlyAsync(Parse.Month(ZusMonth, "Miesiąc wypłat")));
    [RelayCommand] private Task Pit4RAsync() => ShowReportAsync(r => r.Pit4RAsync(Parse.Int(ReportYear, "Rok")));
    [RelayCommand] private Task Pit11Async() => ShowReportAsync(r => r.Pit11Async(Parse.Int(ReportYear, "Rok")));

    [RelayCommand]
    private Task ExportReportPdfAsync() => RunAsync(async () =>
    {
        if (_currentReport is null) throw new DomainException("Najpierw wygeneruj zestawienie.");
        var name = string.Concat(_currentReport.Title.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-');
        var target = await Dialogs.SaveFileAsync("Zapisz PDF", $"{name}.pdf", new FileFilter("PDF", "*.pdf"));
        if (target is null) return;
        await File.WriteAllBytesAsync(target, new PdfReportExporter().Export(_currentReport));
        StatusMessage = $"Zapisano {target}";
    });
}
