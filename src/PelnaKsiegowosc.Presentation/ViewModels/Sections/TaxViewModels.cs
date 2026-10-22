using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Jpk;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>VAT: rejestry, wyliczenie deklaracji, kontrole, rozliczenie na kontach, plik JPK_V7M.</summary>
public sealed partial class VatViewModel : SectionViewModel
{
    public VatViewModel(AppSession s, IDialogService d) : base(s, d, "VAT", "%") { }

    [ObservableProperty] private string _period = "";
    [ObservableProperty] private TableData _sales = TableData.Empty;
    [ObservableProperty] private TableData _purchases = TableData.Empty;
    [ObservableProperty] private TableData _declaration = TableData.Empty;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _carriedForward = "";
    [ObservableProperty] private string _refundRequested = "";
    [ObservableProperty] private bool _correction;

    private DateOnly PeriodDate => Parse.Month(Period, "Okres");

    protected override async Task LoadCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(Period))
        {
            var t = Session.Clock.Today.AddMonths(-1);
            Period = $"{t.Month:00}.{t.Year}";
        }
        await using var db = Session.OpenDb();
        var vat = new VatService(db, Session.User, Session.Clock);
        Sales = Register(await vat.RegisterAsync(VatRegisterType.Sprzedaz, PeriodDate), false);
        Purchases = Register(await vat.RegisterAsync(VatRegisterType.Zakup, PeriodDate), true);
        var decl = await vat.ComputeDeclarationAsync(PeriodDate, Parse.OptionalAmount(CarriedForward, "Nadwyżka z poprzedniej deklaracji"));
        var t2 = new TableData(new TableColumn("Pole"), new TableColumn("Kwota", ColumnKind.Money));
        foreach (var (k, v) in decl.Fields) t2.Add(null, k, v);
        Declaration = t2;
        var sb = new StringBuilder();
        sb.AppendLine($"Podatek należny: {Money.Format(decl.OutputTax)}; naliczony: {Money.Format(decl.InputTax)}; nadwyżka z poprzedniego okresu: {Money.Format(decl.CarriedForwardFromPrevious)}");
        sb.AppendLine(decl.Payable > 0 ? $"Do wpłaty: {Money.Format(decl.Payable)} zł" : $"Nadwyżka naliczonego nad należnym: {Money.Format(decl.Surplus)} zł");
        foreach (var w in decl.Warnings) sb.AppendLine("UWAGA: " + w);
        Summary = sb.ToString();
    }

    private static TableData Register(List<VatRegisterEntry> list, bool purchase)
    {
        var t = new TableData(new TableColumn("Lp.", ColumnKind.Integer, 0.4), new TableColumn("Dokument", Width: 1.3), new TableColumn("Data wyst.", ColumnKind.Date, 0.8),
            new TableColumn(purchase ? "Data wpływu" : "Data sprzedaży", ColumnKind.Date, 0.8), new TableColumn("Kontrahent", Width: 2), new TableColumn("NIP", Width: 0.9),
            new TableColumn("Stawka", Width: 0.5), new TableColumn("Netto", ColumnKind.Money), new TableColumn("VAT", ColumnKind.Money),
            new TableColumn(purchase ? "Do odliczenia" : "Pola JPK", purchase ? ColumnKind.Money : ColumnKind.Text), new TableColumn("Oznaczenia", Width: 0.8));
        foreach (var e in list.OrderBy(e => e.OrdinalNumber))
            t.Add(e.DocumentId, e.OrdinalNumber, e.DocumentNumber, e.IssueDate, purchase ? e.ReceiptDate : e.OperationDate, e.CounterpartyName, e.CounterpartyNip,
                e.VatRateCode, e.NetPln, e.VatPln, purchase ? e.DeductibleVatPln : $"{e.JpkNetField}/{e.JpkTaxField}", string.Join(" ", new[] { e.GtuCodes, e.Procedures, e.JpkDocumentType }.Where(x => !string.IsNullOrEmpty(x))));
        var total = t.Add(null, null, "RAZEM", null, null, null, null, null, list.Sum(e => e.NetPln), list.Sum(e => e.VatPln), purchase ? list.Sum(e => e.DeductibleVatPln) : null, null);
        t.Rows.Remove(total);
        t.AddRow(new TableRow(null, total.Cells) { IsTotal = true });
        return t;
    }

    [RelayCommand] private Task ShowPeriodAsync() => LoadAsync();

    [RelayCommand]
    private Task ValidateAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var problems = await new VatService(db, Session.User, Session.Clock).ValidateRegistersAsync(PeriodDate);
        await Dialogs.ShowTextAsync("Kontrola rejestrów VAT", problems.Count == 0 ? "Nie stwierdzono nieprawidłowości." : string.Join(Environment.NewLine, problems));
    });

    [RelayCommand]
    private Task PostSettlementAsync() => ChangeAsync(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Rozliczenie VAT", $"Zaksięgować rozliczenie VAT za {Period} (przeksięgowanie podatku należnego i naliczonego na rozrachunek z urzędem)?")) return;
        await using var db = Session.OpenDb();
        var e = await new VatService(db, Session.User, Session.Clock).PostSettlementAsync(PeriodDate, Parse.Amount(RefundRequested, "Kwota do zwrotu"));
        StatusMessage = $"Zaksięgowano {e.JournalNumber}";
    }, "Rozliczenie VAT zaksięgowane.");

    [RelayCommand]
    private Task GenerateJpkAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var vat = new VatService(db, Session.User, Session.Clock);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == Session.CompanyId);
        var sales = await vat.RegisterAsync(VatRegisterType.Sprzedaz, PeriodDate);
        var purchases = await vat.RegisterAsync(VatRegisterType.Zakup, PeriodDate);
        var decl = await vat.ComputeDeclarationAsync(PeriodDate, Parse.OptionalAmount(CarriedForward, "Nadwyżka z poprzedniej deklaracji"));
        var r = JpkV7Builder.Build(company, PeriodDate, sales, purchases, decl, Correction, Session.Clock.UtcNow, Session.Validator);
        var target = await Dialogs.SaveFileAsync("Zapisz JPK_V7M", $"JPK_V7M-{PeriodDate:yyyy-MM}{(Correction ? "-korekta" : "")}.xml", new FileFilter("XML", "*.xml"));
        if (target is null) return;
        r.Document.Save(target);
        await Dialogs.ShowTextAsync("JPK_V7M", $"Zapisano: {target}{Environment.NewLine}Status: {r.Status}{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, r.Warnings.Concat(r.Validation.Errors.Take(30))) +
            $"{Environment.NewLine}{Environment.NewLine}Plik wymaga weryfikacji i podpisania przed wysyłką do urzędu — program nie wysyła JPK.");
    });
}

/// <summary>CIT: uzgodnienie wyniku bilansowego z podatkowym, korekty, zaliczki narastająco.</summary>
public sealed partial class CitViewModel : SectionViewModel
{
    public CitViewModel(AppSession s, IDialogService d) : base(s, d, "CIT", "§") { }

    [ObservableProperty] private string _applicability = "";
    [ObservableProperty] private bool _isApplicable = true;
    [ObservableProperty] private string _uptoPeriod = "";
    [ObservableProperty] private TableData _reconciliation = TableData.Empty;
    [ObservableProperty] private TableData _advances = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedAdvance;
    [ObservableProperty] private string _warnings = "";

    [ObservableProperty] private Choice<CitAdjustmentType> _adjustmentType = Choices.Of<CitAdjustmentType>()[0];
    [ObservableProperty] private string _adjustmentPeriod = "1";
    [ObservableProperty] private string _adjustmentAmount = "";
    [ObservableProperty] private string _adjustmentDescription = "";
    [ObservableProperty] private string _adjustmentBasis = "";
    public List<Choice<CitAdjustmentType>> AdjustmentTypes { get; } = Choices.Of<CitAdjustmentType>();

    protected override async Task LoadCoreAsync()
    {
        var form = Session.Company!.LegalForm;
        IsApplicable = LegalForms.IsCitTaxpayer(form);
        Applicability = IsApplicable
            ? $"Jednostka ({LegalForms.Name(form)}) jest podatnikiem CIT. Wyliczenia są pomocnicze i wymagają weryfikacji — pozycje „do analizy” są sygnalizowane."
            : $"Jednostka ({LegalForms.Name(form)}) nie jest podatnikiem CIT — dochód opodatkowują wspólnicy/właściciel (PIT). Program nie wylicza zaliczek PIT; uzgodnienie wyniku poniżej ma charakter pomocniczy.";
        var year = Session.CurrentYear;
        if (string.IsNullOrWhiteSpace(UptoPeriod))
            UptoPeriod = (year.Periods.LastOrDefault(p => p.EndDate < Session.Clock.Today)?.Number ?? 1).ToString();
        await using var db = Session.OpenDb();
        var svc = new CitService(db, Session.User, Session.Clock);
        var rec = await svc.ReconcileAsync(year.Id, Parse.Int(UptoPeriod, "Okres"));
        var t = new TableData(new TableColumn("Pozycja", Width: 3), new TableColumn("Kwota", ColumnKind.Money), new TableColumn("Podstawa / uwagi", Width: 2));
        t.Add(null, $"Przychody bilansowe ({rec.From.ToPl()}–{rec.To.ToPl()})", rec.BookRevenue, "");
        t.Add(null, "Koszty bilansowe", rec.BookCosts, "");
        t.Add(null, "Wynik bilansowy przed opodatkowaniem", rec.BookResultBeforeTax, "");
        foreach (var a in rec.Adjustments) t.Add(null, "  " + a.Item, a.Amount, a.Basis);
        t.Add(null, "Przychody podatkowe", rec.TaxRevenue, "");
        t.Add(null, "Koszty uzyskania przychodów", rec.TaxCosts, "");
        t.Add(null, "Dochód", rec.Computation.Income, "");
        t.Add(null, "Strata", rec.Computation.Loss, "");
        t.Add(null, "Odliczenia od dochodu", rec.Deductions, "");
        t.Add(null, "Podstawa opodatkowania", rec.Computation.TaxBase, "zaokrąglona do pełnych złotych");
        t.Add(null, $"Podatek ({rec.Computation.RatePercent:0.##}%)", rec.Computation.Tax, "");
        foreach (var (acc, name, amount) in rec.ToAnalyze) t.Add(null, $"DO ANALIZY: {acc} {name}", amount, "wymaga oceny podatkowej");
        Reconciliation = t;
        Warnings = string.Join(Environment.NewLine, rec.Warnings);

        var adv = new TableData(new TableColumn("Okres", ColumnKind.Integer, 0.4), new TableColumn("Koniec okresu", ColumnKind.Date), new TableColumn("Dochód narastająco", ColumnKind.Money),
            new TableColumn("Podstawa", ColumnKind.Money), new TableColumn("Podatek narastająco", ColumnKind.Money), new TableColumn("Wpłacono wcześniej", ColumnKind.Money),
            new TableColumn("Zaliczka", ColumnKind.Money), new TableColumn("Termin", ColumnKind.Date), new TableColumn("Zatwierdzona", Width: 0.6));
        foreach (var a in await db.CitAdvances.AsNoTracking().Where(a => a.FiscalYearId == year.Id).OrderBy(a => a.PeriodNumber).ToListAsync())
            adv.Add(a.Id, a.PeriodNumber, a.PeriodEnd, a.CumulativeIncome, a.TaxBase, a.CumulativeTax, a.PreviouslyDue, a.AdvanceDue, a.PaymentDeadline, a.IsApproved);
        Advances = adv;
    }

    [RelayCommand] private Task RecalculateAsync() => LoadAsync();

    [RelayCommand]
    private Task AddAdjustmentAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).AddAdjustmentAsync(Session.CurrentYear.Id, Parse.Int(AdjustmentPeriod, "Okres korekty"), AdjustmentType.Value,
            Parse.Amount(AdjustmentAmount, "Kwota"), AdjustmentDescription, Parse.Trim(AdjustmentBasis));
        AdjustmentAmount = AdjustmentDescription = AdjustmentBasis = "";
    }, "Dodano korektę wyniku podatkowego.");

    [RelayCommand]
    private Task ComputeAdvanceAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var a = await new CitService(db, Session.User, Session.Clock).ComputeAdvanceAsync(Session.CurrentYear.Id, Parse.Int(UptoPeriod, "Okres"));
        StatusMessage = $"Zaliczka za okres {a.PeriodNumber}: {Money.Format(a.AdvanceDue)} zł, termin {a.PaymentDeadline.ToPl()}";
    }, "Zapisano wyliczenie zaliczki.");

    [RelayCommand]
    private Task ApproveAdvanceAsync() => ChangeAsync(async () =>
    {
        if (SelectedAdvance?.Tag is not Guid id) throw new DomainException("Zaznacz zaliczkę na liście.");
        var confirmed = await Dialogs.ConfirmAsync("Zatwierdzenie zaliczki", "Czy pozycje oznaczone „do analizy” zostały zweryfikowane, a korekty wyniku podatkowego wprowadzone?");
        if (!confirmed) return;
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).ApproveAdvanceAsync(id, true);
    }, "Zaliczka zatwierdzona.");
}
