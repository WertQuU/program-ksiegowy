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
        if (decl.Quarterly) sb.AppendLine($"Deklaracja kwartalna (JPK_V7K) za {decl.Quarter} kwartał {decl.Period.Year} — suma trzech miesięcy.");
        else if (!await vat.IsDeclarationMonthAsync(PeriodDate)) sb.AppendLine("Rozliczenie kwartalne: w tym miesiącu składasz JPK_V7K bez deklaracji (sama ewidencja). Kwoty poniżej — tylko ten miesiąc.");
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
        if (await JpkExport.GenerateAsync(Session, Dialogs, PeriodDate, Parse.OptionalAmount(CarriedForward, "Nadwyżka z poprzedniej deklaracji"), Correction))
            StatusMessage = "Wygenerowano JPK_V7M.";
    });
}

/// <summary>CIT: uzgodnienie wyniku bilansowego z podatkowym, korekty, zaliczki narastająco.</summary>
public sealed partial class CitViewModel : SectionViewModel
{
    public CitViewModel(AppSession s, IDialogService d) : base(s, d, "CIT", "§") { }

    [ObservableProperty] private string _applicability = "";
    [ObservableProperty] private bool _isApplicable = true;
    [ObservableProperty] private string _uptoPeriod = "";
    [ObservableProperty] private string _eurRate = "";
    [ObservableProperty] private TableData _losses = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedLoss;
    [ObservableProperty] private string _lossYear = "";
    [ObservableProperty] private string _lossYearEnd = "";
    [ObservableProperty] private bool _lossCapital;
    [ObservableProperty] private string _lossAmount = "";
    [ObservableProperty] private string _lossUntil = "";
    [ObservableProperty] private string _lossDeduction = "";
    [ObservableProperty] private string _lossFromPeriod = "1";
    [ObservableProperty] private bool _lossOneTime;
    [ObservableProperty] private string _statusEurRate = "";
    [ObservableProperty] private string _rateLabels = "";
    [ObservableProperty] private bool _reducedRateExcluded;
    [ObservableProperty] private bool _jpkCit2025;
    [ObservableProperty] private TableData _reconciliation = TableData.Empty;
    [ObservableProperty] private TableData _advances = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedAdvance;
    [ObservableProperty] private string _warnings = "";

    [ObservableProperty] private Choice<CitAdjustmentType> _adjustmentType = Choices.Of<CitAdjustmentType>()[0];
    [ObservableProperty] private string _adjustmentPeriod = "1";
    [ObservableProperty] private string _adjustmentAmount = "";
    [ObservableProperty] private string _adjustmentDescription = "";
    [ObservableProperty] private string _adjustmentBasis = "";
    /// <summary>Rodzaj różnicy (RPD JPK_CIT): 1 — trwała, 2 — przejściowa.</summary>
    [ObservableProperty] private Choice<int> _adjustmentDifference = DifferenceKinds[0];
    [ObservableProperty] private bool _adjustmentPriorYears;
    public static List<Choice<int>> DifferenceKinds { get; } = new() { new(0, "rodzaj różnicy: —"), new(1, "różnica trwała"), new(2, "różnica przejściowa") };
    public List<Choice<CitAdjustmentType>> AdjustmentTypes { get; } = Choices.Of<CitAdjustmentType>();

    [RelayCommand]
    private async Task DeferredTaxAsync()
    {
        var y = Session.CurrentYear;
        await RunAsync(async () =>
        {
            await using var db = Session.OpenDb();
            var policy = await db.AccountingPolicies.AsNoTracking().Where(p => p.CompanyId == Session.CompanyId).OrderByDescending(p => p.Version).FirstOrDefaultAsync();
            var svc = new DeferredTaxService(db, Session.User, Session.Clock);
            var c = await svc.ComputeAsync(y.Id, y.EndDate); // straty podatkowe — wyłącznie z rejestru strat (z oceną wykorzystania)
            var text = string.Join(Environment.NewLine, c.Differences.Select(d => $"{d.Item}: bilansowa {Money.Format(d.BookValue)}, podatkowa {Money.Format(d.TaxValue)}, różnica {Money.Format(d.Difference)} × {d.RatePercent:0}% ({(d.ReversalUnknown ? "rok odwrócenia nieustalony" : $"odwrócenie w roku od {d.ReversalPeriodStart.ToPl()}")}) = {Money.Format(d.Tax)}{(d.Unconfirmed ? " [niezatwierdzone]" : "")} ({d.Basis})")) +
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"Dodatnie różnice przejściowe: {Money.Format(c.TaxableDifferences)} → rezerwa {Money.Format(c.Provision)} (zaksięgowano {Money.Format(c.BookedProvision)}){Environment.NewLine}" +
                $"Ujemne różnice przejściowe: {Money.Format(c.DeductibleDifferences)} → aktywo {Money.Format(c.Asset)} (zaksięgowano {Money.Format(c.BookedAsset)}){Environment.NewLine}" +
                $"Wpływ na wynik (podatek odroczony): {Money.Format(c.ResultCharge)}" +
                (c.UnconfirmedTax > 0 ? $"{Environment.NewLine}W tym oparte na niezatwierdzonych prognozach: {Money.Format(c.UnconfirmedTax)} — powyżej progu istotności księgowanie jest zablokowane." : "") +
                (c.Warnings is { Count: > 0 } w ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, w) : "") +
                (policy is { IsMicroEntity: true } or { IsSmallEntity: true } ? $"{Environment.NewLine}{Environment.NewLine}Jednostka mała/mikro może nie ustalać podatku odroczonego (art. 37 ust. 10 UoR) — zgodnie z polityką rachunkowości." : "");
            if (c.ProvisionChange == 0 && c.AssetChange == 0) { await Dialogs.ShowTextAsync("Podatek odroczony", text + Environment.NewLine + Environment.NewLine + "Stan zaksięgowany jest aktualny."); return; }
            if (!await Dialogs.ConfirmAsync("Podatek odroczony", text + Environment.NewLine + Environment.NewLine + "Zaksięgować zmianę (841 / 842 / 872)?")) return;
            await svc.PostAsync(y.Id, y.EndDate);
            StatusMessage = "Podatek odroczony zaksięgowany.";
        });
    }

    [ObservableProperty] private string _expectedRate = "19";
    [ObservableProperty] private string _expectedRateJustification = "";
    /// <summary>Początek roku podatkowego, którego dotyczy prognoza (domyślnie rok następny).</summary>
    [ObservableProperty] private string _expectedRateYearStart = "";
    [ObservableProperty] private string _calendarConfirmedThrough = "";
    [ObservableProperty] private string _calendarJustification = "";
    [ObservableProperty] private string _deferredMateriality = "";
    [ObservableProperty] private string _deferredMaterialityJustification = "";

    /// <summary>Potwierdzenie granic przyszłych lat podatkowych (12 miesięcy, art. 8 ust. 1 ustawy o CIT) dla prognozy podatku odroczonego.</summary>
    [RelayCommand]
    private Task ConfirmTaxCalendarAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new DeferredTaxService(db, Session.User, Session.Clock).ConfirmTaxYearCalendarAsync(Parse.Date(CalendarConfirmedThrough, "Potwierdzone do"), CalendarJustification);
    }, "Potwierdzono kalendarz lat podatkowych.");

    [ObservableProperty] private string _reversalAssetNumber = "";
    [ObservableProperty] private string _reversalYearStart = "";
    [ObservableProperty] private string _reversalJustification = "";

    /// <summary>Rok odwrócenia części różnicy środka trwałego, której nie odwracają plany amortyzacji (np. planowana sprzedaż).</summary>
    [RelayCommand]
    private Task SaveReversalYearAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var number = ReversalAssetNumber.Trim();
        var asset = await db.FixedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == Session.CompanyId && a.InventoryNumber == number)
                    ?? throw new DomainException($"Brak środka trwałego o numerze „{number}”.");
        await new DeferredTaxService(db, Session.User, Session.Clock).SetReversalYearAsync(asset.Id, Parse.Date(ReversalYearStart, "Rok odwrócenia od"), ReversalJustification);
    }, "Zapisano rok odwrócenia różnicy.");

    [RelayCommand]
    private Task SaveDeferredMaterialityAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new DeferredTaxService(db, Session.User, Session.Clock).SetMaterialityAsync(Parse.Amount(DeferredMateriality, "Próg"), DeferredMaterialityJustification);
    }, "Zapisano próg istotności podatku odroczonego.");

    /// <summary>Prognoza stawki CIT na rok odwrócenia różnic przejściowych (art. 37 ust. 6 UoR).</summary>
    [RelayCommand]
    private Task SaveExpectedRateAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new DeferredTaxService(db, Session.User, Session.Clock).SetExpectedRateAsync(Parse.OptionalDate(ExpectedRateYearStart, "Rok od") ?? Session.CurrentYear.EndDate.AddDays(1), Parse.Amount(ExpectedRate, "Stawka"), ExpectedRateJustification);
    }, "Zapisano prognozę stawki dla podatku odroczonego.");

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
        var liabilities = new TableData(new TableColumn("Dokument", Width: 2), new TableColumn("Termin", ColumnKind.Date), new TableColumn("Kwota", ColumnKind.Money));
        foreach (var item in await db.OpenItems.AsNoTracking().Where(o => o.CompanyId == Session.CompanyId && o.Side == Domain.Settlements.OpenItemSide.Zobowiazanie).OrderBy(o => o.DueDate).ToListAsync())
            liabilities.Add(item.Id, item.Reference, item.DueDate, item.AmountPln);
        Liabilities = liabilities;
        var manual = new TableData(new TableColumn("Okres", ColumnKind.Integer), new TableColumn("Opis", Width: 2), new TableColumn("Kwota", ColumnKind.Money), new TableColumn("Zobowiązanie"));
        foreach (var adjustment in await db.CitAdjustments.AsNoTracking().Where(a => a.FiscalYearId == year.Id).OrderBy(a => a.PeriodNumber).ToListAsync())
            manual.Add(adjustment.Id, adjustment.PeriodNumber, adjustment.Description, adjustment.Amount,
                adjustment.OpenItemId is { } itemId ? await db.OpenItems.Where(o => o.Id == itemId).Select(o => o.Reference).FirstAsync() : "Niepowiązana");
        ManualAdjustments = manual;
        var pl = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");
        EurRate = (await svc.LimitEurRateAsync(year.StartDate))?.ToString("0.0000", pl) ?? "";
        var october = await svc.StatusOctoberAsync(year);
        StatusEurRate = (await svc.StatusEurRateAsync(october.Year))?.ToString("0.0000", pl) ?? "";
        RateLabels = $"Limit 9% (przychody {year.Name}): kurs z 1. dnia roboczego roku podatkowego od {year.StartDate.ToPl()}. Status małego podatnika: kurs z 1. dnia roboczego października poprzedniego roku podatkowego ({october.ToPl()}).";
        ReducedRateExcluded = await svc.ReducedRateExcludedAsync();
        JpkCit2025 = await svc.JpkCit2025Async();
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
        if (rec.CapitalGains.TaxRevenue != 0 || rec.CapitalGains.TaxCosts != 0)
        {
            t.Add(null, "  w tym inne źródła — dochód", rec.Operating.Income, $"podatek {rec.Operating.RatePercent:0}%: {Money.Format(rec.Operating.Tax)}");
            t.Add(null, "  w tym zyski kapitałowe — dochód", rec.CapitalGains.Income, $"art. 7b, podatek 19%: {Money.Format(rec.CapitalGains.Tax)}");
        }
        t.Add(null, $"Podatek ({rec.Computation.RatePercent:0.##}%{(rec.CapitalGains.Tax != 0 ? " + zyski kapitałowe 19%" : "")})", rec.Computation.Tax, rec.Rate.Reason);
        foreach (var (acc, name, amount) in rec.ToAnalyze) t.Add(null, $"DO ANALIZY: {acc} {name}", amount, "wymaga oceny podatkowej");
        Reconciliation = t;
        Warnings = string.Join(Environment.NewLine, rec.Warnings);

        var adv = new TableData(new TableColumn("Okres", ColumnKind.Integer, 0.4), new TableColumn("Koniec okresu", ColumnKind.Date), new TableColumn("Dochód narastająco", ColumnKind.Money),
            new TableColumn("Podstawa", ColumnKind.Money), new TableColumn("Podatek narastająco", ColumnKind.Money), new TableColumn("Wpłacono wcześniej", ColumnKind.Money),
            new TableColumn("Zaliczka", ColumnKind.Money), new TableColumn("Termin", ColumnKind.Date), new TableColumn("Zatwierdzona", Width: 0.6));
        foreach (var a in await db.CitAdvances.AsNoTracking().Where(a => a.FiscalYearId == year.Id).OrderBy(a => a.PeriodNumber).ToListAsync())
            adv.Add(a.Id, a.PeriodNumber, a.PeriodEnd, a.CumulativeIncome, a.TaxBase, a.CumulativeTax, a.PreviouslyDue, a.AdvanceDue, a.PaymentDeadline, a.IsApproved);
        Advances = adv;

        var losses = new TableData(new TableColumn("Rok straty"), new TableColumn("Źródło", Width: 1.2), new TableColumn("Strata", ColumnKind.Money),
            new TableColumn("Odliczono", ColumnKind.Money), new TableColumn("Pozostało", ColumnKind.Money), new TableColumn("Odliczenie do"));
        foreach (var (l, used, until, projected) in await svc.LossesAsync())
            losses.Add(l.Id, l.YearLabel, l.CapitalGains ? "zyski kapitałowe" : "inne źródła", l.Amount, used, l.Amount - used,
                until is { } u ? u.ToPl() + (projected ? " (prognoza — przeliczana po utworzeniu kolejnych lat)" : "") : "do potwierdzenia");
        Losses = losses;
        var excess = new TableData(new TableColumn("Rok nadwyżki"), new TableColumn("Źródło", Width: 1.2), new TableColumn("Nadwyżka", ColumnKind.Money),
            new TableColumn("Zaliczono", ColumnKind.Money), new TableColumn("Pozostało", ColumnKind.Money), new TableColumn("Rozliczenie do"));
        foreach (var (c, used, until) in await svc.DebtFinancingCarryforwardsAsync())
            excess.Add(c.Id, c.YearLabel, c.CapitalGains ? "zyski kapitałowe" : "inne źródła", c.Amount, used, c.Amount - used, until?.ToPl() ?? "do potwierdzenia");
        DebtExcess = excess;
        var full = await svc.ReconcileAsync(year.Id, year.Periods.Count);
        DebtHeadroom = $"Art. 15c ust. 3: wolny limit kosztów finansowania w roku {year.Name}: {Money.Format(full.DebtFinancingHeadroom)} zł; nadwyżka roku: {Money.Format(full.DebtFinancingExcessOperating + full.DebtFinancingExcessCapital)} zł.";
    }

    [ObservableProperty] private TableData _debtExcess = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedDebtExcess;
    [ObservableProperty] private string _debtHeadroom = "";
    [ObservableProperty] private string _debtExcessYear = "";
    [ObservableProperty] private string _debtExcessYearEnd = "";
    [ObservableProperty] private bool _debtExcessCapital;
    [ObservableProperty] private string _debtExcessAmount = "";
    [ObservableProperty] private string _debtExcessUntil = "";
    [ObservableProperty] private string _debtExcessUse = "";

    /// <summary>Nadwyżka kosztów finansowania dłużnego z lat sprzed prowadzenia ksiąg w programie (z CIT-8).</summary>
    [RelayCommand]
    private Task AddDebtExcessAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).AddDebtFinancingCarryforwardAsync(DebtExcessYear, Parse.Date(DebtExcessYearEnd, "Koniec roku nadwyżki"), DebtExcessCapital,
            Parse.Amount(DebtExcessAmount, "Nadwyżka"), "wprowadzona ręcznie (zeznanie CIT-8)", Parse.OptionalDate(DebtExcessUntil, "Rozliczenie do"));
        DebtExcessYear = DebtExcessYearEnd = DebtExcessAmount = DebtExcessUntil = "";
    }, "Zarejestrowano nadwyżkę kosztów finansowania dłużnego.");

    [RelayCommand]
    private Task RegisterPreviousYearDebtExcessAsync() => ChangeAsync(async () =>
    {
        var prev = Session.Years.LastOrDefault(y => y.EndDate < Session.CurrentYear.StartDate) ?? throw new DomainException("Brak poprzedniego roku obrotowego w programie — wprowadź nadwyżkę ręcznie.");
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).RegisterDebtFinancingExcessFromYearAsync(prev.Id);
    }, "Zarejestrowano nadwyżkę z poprzedniego roku.");

    [RelayCommand]
    private Task UseDebtExcessAsync() => ChangeAsync(async () =>
    {
        if (SelectedDebtExcess?.Tag is not Guid id) throw new DomainException("Zaznacz nadwyżkę na liście.");
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).UseDebtFinancingCarryforwardAsync(id, Session.CurrentYear.Id, Parse.Amount(DebtExcessUse, "Kwota"));
        DebtExcessUse = "";
    }, "Zaliczono nadwyżkę do kosztów bieżącego roku (rozliczenie roczne).");

    [RelayCommand]
    private Task ConfirmPreviousSalesAsync() => ChangeAsync(async () =>
    {
        var value = await Dialogs.PromptAsync("Status małego podatnika", "Podaj przychody ze sprzedaży wraz z VAT poprzedniego roku z kompletnej ewidencji podatkowej (także poza fakturami w programie):", "");
        if (value is null) return;
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).SetPreviousSalesAsync(Session.CurrentYear.Id, Parse.Amount(value, "Sprzedaż z VAT"));
    }, "Zapisano potwierdzoną sprzedaż poprzedniego roku.");

    [ObservableProperty] private bool _linkAdjustmentToLiability;
    [ObservableProperty] private bool _adjustmentCapitalGains;
    [ObservableProperty] private TableData _manualAdjustments = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedManualAdjustment;

    [RelayCommand]
    private Task LinkManualLiabilityAsync() => ChangeAsync(async () =>
    {
        if (SelectedManualAdjustment?.Tag is not Guid adjustment || SelectedLiability?.Tag is not Guid item)
            throw new DomainException("Wybierz korektę ręczną i zobowiązanie w zakładce art. 18f.");
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).LinkManualLiabilityAsync(adjustment, item);
    }, "Powiązano korektę ze zobowiązaniem.");

    [RelayCommand]
    private Task RemoveManualAdjustmentAsync() => ChangeAsync(async () =>
    {
        if (SelectedManualAdjustment?.Tag is not Guid id) throw new DomainException("Zaznacz korektę ręczną.");
        if (!await Dialogs.ConfirmAsync("Usunięcie korekty", "Usunąć ręczną korektę? Jej treść zostanie zachowana w audycie. Następnie przelicz zaliczki; możesz zakwalifikować zobowiązanie do automatycznej korekty.")) return;
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).RemoveManualAdjustmentAsync(id);
    }, "Usunięto korektę; przelicz zaliczki.");

    [ObservableProperty] private TableData _liabilities = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedLiability;

    [RelayCommand]
    private Task QualifyLiabilityAsync() => ChangeAsync(async () =>
    {
        if (SelectedLiability?.Tag is not Guid id) throw new DomainException("Zaznacz zobowiązanie na liście art. 18f.");
        var applicable = await Dialogs.ConfirmAsync("Kwalifikacja art. 18f", "Czy potwierdzasz transakcję handlową, ujęcie w KUP, opodatkowanie stron i spełnienie warunków oraz brak wyłączeń art. 18f (w tym terminu ustawowego)? Tak — podlega; Nie — nie podlega.");
        var reason = await Dialogs.PromptAsync("Uzasadnienie kwalifikacji", "Opisz sprawdzone warunki i wyłączenia. Anuluj, aby pozostawić kwalifikację nierozstrzygniętą:", "");
        if (reason is null) return;
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).QualifyLiabilityAsync(id, applicable, reason);
    }, "Zapisano kwalifikację art. 18f.");

    [RelayCommand] private Task RecalculateAsync() => LoadAsync();

    /// <summary>Strata z lat sprzed prowadzenia ksiąg w programie (z zeznania CIT-8).</summary>
    [RelayCommand]
    private Task AddLossAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).AddLossAsync(LossYear, Parse.Date(LossYearEnd, "Koniec roku straty"), LossCapital, Parse.Amount(LossAmount, "Strata"), "wprowadzona ręcznie (zeznanie CIT-8)",
            deductibleUntil: Parse.OptionalDate(LossUntil, "Koniec okresu odliczenia"));
        LossYear = LossYearEnd = LossAmount = "";
    }, "Zarejestrowano stratę podatkową.");

    [RelayCommand]
    private Task RegisterPreviousYearLossAsync() => ChangeAsync(async () =>
    {
        var prev = Session.Years.LastOrDefault(y => y.EndDate < Session.CurrentYear.StartDate) ?? throw new DomainException("Brak poprzedniego roku obrotowego w programie — wprowadź stratę ręcznie.");
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).RegisterLossesFromYearAsync(prev.Id);
    }, "Zarejestrowano stratę z poprzedniego roku.");

    /// <summary>Udokumentowana ocena możliwości wykorzystania straty — warunek ujęcia aktywa z tytułu odroczonego podatku (art. 37 ust. 4 UoR).</summary>
    [RelayCommand]
    private Task AssessLossAsync() => ChangeAsync(async () =>
    {
        if (SelectedLoss?.Tag is not Guid id) throw new DomainException("Zaznacz stratę na liście.");
        var justification = await Dialogs.PromptAsync("Ocena wykorzystania straty", "Podstawa oceny, że strata zostanie odliczona (prognoza dochodów, umowy, plan). Pozostaw puste, aby przerwać:");
        if (string.IsNullOrWhiteSpace(justification)) return;
        var amountText = await Dialogs.PromptAsync("Ocena wykorzystania straty", "Kwota straty możliwa do wykorzystania (puste — cała pozostała; 0 — wykorzystanie niepewne):");
        await using var db = Session.OpenDb();
        var amount = Parse.OptionalAmount(amountText, "Kwota");
        await new DeferredTaxService(db, Session.User, Session.Clock).AssessLossAsync(id, amount is not 0m, amount, justification);
    }, "Zapisano ocenę wykorzystania straty.");

    [RelayCommand]
    private Task ConfirmLossWindowAsync() => ChangeAsync(async () =>
    {
        if (SelectedLoss?.Tag is not Guid id) throw new DomainException("Zaznacz stratę na liście.");
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).ConfirmLossWindowAsync(id, Parse.Date(LossUntil, "Koniec okresu odliczenia"));
    }, "Zapisano koniec okresu odliczenia straty.");

    [RelayCommand]
    private Task DeductLossAsync() => ChangeAsync(async () =>
    {
        if (SelectedLoss?.Tag is not Guid id) throw new DomainException("Zaznacz stratę na liście.");
        await using var db = Session.OpenDb();
        await new CitService(db, Session.User, Session.Clock).DeductLossAsync(id, Session.CurrentYear.Id, Parse.Int(LossFromPeriod, "Od okresu"), Parse.Amount(LossDeduction, "Kwota odliczenia"), LossOneTime);
        LossDeduction = "";
    }, "Odliczenie straty uwzględniono w wyliczeniu CIT.");

    [RelayCommand]
    private Task SaveRateConditionsAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var svc = new CitService(db, Session.User, Session.Clock);
        var y = Session.CurrentYear;
        if (!string.IsNullOrWhiteSpace(EurRate)) await svc.SetLimitEurRateAsync(y.StartDate, ParseRate(EurRate, "Kurs EUR (limit 9%)"));
        if (!string.IsNullOrWhiteSpace(StatusEurRate)) await svc.SetStatusEurRateAsync((await svc.StatusOctoberAsync(y)).Year, ParseRate(StatusEurRate, "Kurs EUR (mały podatnik)"));
        await svc.SetReducedRateExcludedAsync(ReducedRateExcluded);
        await svc.SetJpkCit2025Async(JpkCit2025);
    }, "Zapisano warunki stawki 9%.");

    private static decimal ParseRate(string text, string field) =>
        decimal.TryParse(text.Trim().Replace('.', ','), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.GetCultureInfo("pl-PL"), out var v)
            ? v : throw new DomainException($"Pole „{field}”: podaj kurs, np. 4,2156.");

    /// <summary>
    /// Gdy brak kursu euro do limitu 9% (pierwszy dzień roboczy roku podatkowego), program próbuje pobrać go z NBP przed
    /// zapisem zaliczki; bez połączenia użytkownik wpisuje kurs ręcznie (zaliczka nie jest liczona po kursie zastępczym).
    /// </summary>
    internal static async Task EnsureLimitRateAsync(AppSession session, Domain.Periods.FiscalYear year)
    {
        await using var db = session.OpenDb();
        var svc = new CitService(db, session.User, session.Clock);
        if (await svc.LimitEurRateAsync(year.StartDate) is not null) return;
        try
        {
            using var http = new HttpClient { BaseAddress = Infrastructure.Nbp.NbpClient.BaseUrl, Timeout = TimeSpan.FromSeconds(15) };
            var r = await new Infrastructure.Nbp.NbpClient(http).RateOnFirstBusinessDayAsync("EUR", year.StartDate);
            await svc.SetLimitEurRateAsync(year.StartDate, r.Mid);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DomainException) { /* komunikat o braku kursu zgłosi wyliczenie */ }
    }

    /// <summary>Pobranie obu kursów z tabeli A NBP (pierwszy dzień roboczy roku podatkowego oraz października roku poprzedniego).</summary>
    [RelayCommand]
    private Task FetchNbpRatesAsync() => ChangeAsync(async () =>
    {
        var y = Session.CurrentYear;
        using var http = new HttpClient { BaseAddress = Infrastructure.Nbp.NbpClient.BaseUrl, Timeout = TimeSpan.FromSeconds(20) };
        var nbp = new Infrastructure.Nbp.NbpClient(http);
        await using var db = Session.OpenDb();
        var svc = new CitService(db, Session.User, Session.Clock);
        var october = await svc.StatusOctoberAsync(y);
        var limit = await nbp.RateOnFirstBusinessDayAsync("EUR", y.StartDate);
        var status = await nbp.RateOnFirstBusinessDayAsync("EUR", october);
        await svc.SetLimitEurRateAsync(y.StartDate, limit.Mid);
        await svc.SetStatusEurRateAsync(october.Year, status.Mid);
        StatusMessage = $"NBP: {limit.Mid:0.0000} (tabela {limit.TableNumber} z {limit.EffectiveDate.ToPl()}), {status.Mid:0.0000} (tabela {status.TableNumber} z {status.EffectiveDate.ToPl()}).";
    }, "Pobrano kursy euro z NBP.");

    [RelayCommand]
    private Task AddAdjustmentAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        if (LinkAdjustmentToLiability && SelectedLiability?.Tag is not Guid)
            throw new DomainException("Zaznacz zobowiązanie w zakładce art. 18f.");
        await new CitService(db, Session.User, Session.Clock).AddAdjustmentAsync(Session.CurrentYear.Id, Parse.Int(AdjustmentPeriod, "Okres korekty"), AdjustmentType.Value,
            Parse.Amount(AdjustmentAmount, "Kwota"), AdjustmentDescription, Parse.Trim(AdjustmentBasis),
            openItemId: LinkAdjustmentToLiability ? (Guid?)SelectedLiability!.Tag : null, capitalGains: AdjustmentCapitalGains,
            bookedInPriorYears: AdjustmentPriorYears, permanentDifference: AdjustmentDifference.Value switch { 1 => true, 2 => false, _ => null });
        AdjustmentAmount = AdjustmentDescription = AdjustmentBasis = "";
    }, "Dodano korektę wyniku podatkowego.");

    [RelayCommand]
    private Task ComputeAdvanceAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await EnsureLimitRateAsync(Session, Session.CurrentYear);
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

    [RelayCommand]
    private Task JpkCitAsync() => RunAsync(async () =>
    {
        var year = Session.CurrentYear;
        await using var db = Session.OpenDb();
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == Session.CompanyId);
        var fy = await db.FiscalYears.AsNoTracking().Include(f => f.Periods).FirstAsync(f => f.Id == year.Id);
        var rec = await new CitService(db, Session.User, Session.Clock).ReconcileAsync(fy.Id, fy.Periods.Count);
        var r = await JpkKrPdBuilder.BuildAsync(db, company, fy, rec.Rpd, false, Session.Clock.UtcNow, Session.Validator);
        if (rec.Warnings.FirstOrDefault(w => w.StartsWith("RPD")) is { } gap)
            throw new DomainException(gap + " Plik JPK_CIT nie został zapisany — część RPD musi odpowiadać rozliczeniu podatku.");
        var target = await Dialogs.SaveFileAsync("Zapisz JPK_CIT (JPK_KR_PD)", $"JPK_CIT-{year.Name}.xml", new FileFilter("XML", "*.xml"));
        if (target is null) return;
        r.Document.Save(target);
        await new Application.Setup.SettingsService(db, Session.Clock).SetAsync<DateTime?>(Session.CompanyId, $"jpk-cit.wygenerowano.{year.Name}", Session.Clock.UtcNow);
        await db.SaveChangesAsync();
        await Session.AuditAsync("JPK_CIT_WYGENEROWANY", "JPK", null, $"{year.Name}; {r.Status}");
        await Dialogs.ShowTextAsync("JPK_CIT", $"Zapisano: {target}{Environment.NewLine}Status: {r.Status}{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, r.Warnings.Take(40).Concat(r.Validation.Errors.Take(30))) +
            $"{Environment.NewLine}{Environment.NewLine}Termin: do końca 7. miesiąca po roku podatkowym. Plik wyślij bramką JPK MF (wymaga podpisu). Program nie wysyła JPK.");
    });
}
