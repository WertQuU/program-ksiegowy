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

    [ObservableProperty] private TableData _employees = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedEmployee;
    [ObservableProperty] private string _firstName = "";
    [ObservableProperty] private string _lastName = "";
    [ObservableProperty] private string _pesel = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _iban = "";
    [ObservableProperty] private string _taxOffice = "";
    [ObservableProperty] private Choice<ContractType> _contract;
    [ObservableProperty] private Choice<CostsOfIncome> _costs;
    [ObservableProperty] private string _gross = "";
    [ObservableProperty] private string _fraction = "1";
    [ObservableProperty] private string _startDate = "";
    [ObservableProperty] private string _endDate = "";
    [ObservableProperty] private bool _pit2 = true;
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
            FirstName = e.FirstName; LastName = e.LastName; Pesel = e.Pesel ?? ""; Address = e.Address ?? ""; Iban = e.BankAccountIban ?? ""; TaxOffice = e.TaxOfficeCode ?? "";
            Contract = ContractTypes.First(c => c.Value == e.ContractType); Costs = CostTypes.First(c => c.Value == e.Costs);
            Gross = e.Gross.ToString("N2", Money.Pl); Fraction = e.WorkTimeFraction.ToString("0.####", Money.Pl);
            StartDate = e.StartDate.ToPl(); EndDate = e.EndDate?.ToPl() ?? "";
            Pit2 = e.Pit2; YouthRelief = e.YouthRelief; Student = e.StudentUnder26; VoluntarySickness = e.VoluntarySickness; ExemptFromFp = e.ExemptFromFp; Ppk = e.Ppk;
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
        var t = new TableData(new TableColumn("Osoba", Width: 1.8), new TableColumn("Umowa", Width: 1.1), new TableColumn("Brutto", ColumnKind.Money), new TableColumn("ZUS prac.", ColumnKind.Money),
            new TableColumn("Zdrowotna", ColumnKind.Money), new TableColumn("Zaliczka PIT", ColumnKind.Money), new TableColumn("Netto", ColumnKind.Money),
            new TableColumn("Koszt pracodawcy", ColumnKind.Money), new TableColumn("Uwagi", Width: 2.2));
        foreach (var i in list.Items.OrderBy(i => i.EmployeeName))
            t.Add(i.Id, i.EmployeeName, ContractTypes.First(c => c.Value == i.ContractType).Label, i.Gross, i.SocialEmployee, i.Health, i.TaxAdvance, i.Net, i.EmployerCost, i.Notes);
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
            e.Add(x.Id, x.FullName, ContractTypes.First(c => c.Value == x.ContractType).Label, x.Gross, x.StartDate, x.EndDate, x.Pit2, x.IsActive);
        Employees = e;
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
        FirstName = FirstName.Trim(), LastName = LastName.Trim(), Pesel = Parse.Trim(Pesel), Address = Parse.Trim(Address), BankAccountIban = Parse.Trim(Iban),
        TaxOfficeCode = Parse.Trim(TaxOffice), ContractType = Contract.Value, Costs = Costs.Value, Gross = Parse.Amount(Gross, "Wynagrodzenie brutto"),
        WorkTimeFraction = Parse.Amount(Fraction, "Wymiar etatu"), StartDate = Parse.Date(StartDate, "Data rozpoczęcia"), EndDate = Parse.OptionalDate(EndDate, "Data zakończenia"),
        Pit2 = Pit2, YouthRelief = YouthRelief, StudentUnder26 = Student, VoluntarySickness = VoluntarySickness, ExemptFromFp = ExemptFromFp, Ppk = Ppk
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
        var item = await db.PayrollItems.AsNoTracking().FirstAsync(i => i.Id == id);
        var baseGross = Parse.OptionalAmount(NewGross, "Wynagrodzenie za pełny miesiąc")
                        ?? (await db.Employees.AsNoTracking().FirstAsync(x => x.Id == item.EmployeeId)).Gross;
        int Days(string s, string f) => string.IsNullOrWhiteSpace(s) ? 0 : Parse.Int(s, f);
        await new PayrollService(db, Session.User, Session.Clock).ChangeAsync(id, baseGross, Days(SickDays, "Dni choroby"), Days(UnpaidDays, "Dni nieobecności nieodpłatnej"));
        NewGross = SickDays = UnpaidDays = "";
    }, "Przeliczono pozycję listy płac.");

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
