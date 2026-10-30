using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Zakładowy plan kont roku obrotowego: konta, powiązania ze sprawozdaniem, klasyfikacja CIT, zatwierdzenie.</summary>
public sealed partial class ChartViewModel : SectionViewModel
{
    public ChartViewModel(AppSession s, IDialogService d) : base(s, d, "Plan kont", "🗂") { }

    [ObservableProperty] private TableData _accounts = TableData.Empty;
    [ObservableProperty] private TableRow? _selected;
    [ObservableProperty] private string _chartInfo = "";
    [ObservableProperty] private string _filter = "";
    public ChartOptionsForm Options { get; } = new();
    [ObservableProperty] private bool _hasChart;
    private Guid? _chartId;

    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Choice<AccountType> _type = Choices.Of<AccountType>()[0];
    [ObservableProperty] private string _balanceDebitLine = "";
    [ObservableProperty] private string _balanceCreditLine = "";
    [ObservableProperty] private string _profitLossLine = "";
    [ObservableProperty] private bool _settlement;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private Choice<CitClassification> _cit = Choices.Of<CitClassification>()[0];
    public List<Choice<AccountType>> Types { get; } = Choices.Of<AccountType>();
    public List<Choice<CitClassification>> CitClasses { get; } = Choices.Of<CitClassification>();

    // kreator konta — prostymi słowami
    public List<Choice<string>> WizardPurposes { get; } = AccountWizard.Purposes.Select(p => new Choice<string>(p.Key, p.Label)).ToList();
    public List<Choice<TaxAnswer>> TaxAnswers { get; } = new() { new(TaxAnswer.Tak, "Tak — wydatek związany z działalnością"), new(TaxAnswer.Nie, "Nie (np. kary, reprezentacja)"), new(TaxAnswer.NieWiem, "Nie wiem — oznacz do sprawdzenia") };
    [ObservableProperty] private Choice<string>? _wizardPurpose;
    [ObservableProperty] private Choice<TaxAnswer>? _wizardTax;
    [ObservableProperty] private string _wizardName = "";
    [ObservableProperty] private string _wizardHint = "Wybierz, do czego ma służyć konto — program sam nada numer i powiąże je ze sprawozdaniem i CIT.";

    partial void OnWizardPurposeChanged(Choice<string>? value)
    {
        var p = AccountWizard.Purposes.FirstOrDefault(x => x.Key == value?.Value);
        WizardHint = p is null ? "" : $"Przykłady: {p.Explanation}. Konto powstanie jako podkonto {p.Parent}" +
            (p.IsCost || p.IsRevenue ? "; odpowiedz, czy to koszt/przychód podatkowy." : ".");
    }

    [RelayCommand]
    private Task WizardAddAsync() => ChangeAsync(async () =>
    {
        if (WizardPurpose is null) throw new DomainException("Wybierz, do czego ma służyć konto.");
        await using var db = Session.OpenDb();
        var a = await new AccountWizard(db, Session.User, Session.Clock).CreateAsync(ChartId, WizardPurpose.Value, WizardName, (WizardTax ?? TaxAnswers[2]).Value);
        WizardName = "";
        WizardHint = $"Dodano konto {a.Number} „{a.Name}”. Możesz go używać od razu w dokumentach i kategoriach wydatków.";
    }, "Konto dodane.");

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var svc = new ChartService(db, Session.User, Session.Clock);
        var chart = await db.Charts.AsNoTracking().Where(c => c.FiscalYearId == Session.CurrentYear.Id).OrderByDescending(c => c.Version).FirstOrDefaultAsync();
        _chartId = chart?.Id;
        HasChart = chart is not null;
        if (chart is null)
        {
            ChartInfo = $"Rok {Session.CurrentYear.Name} nie ma planu kont. Utwórz go z szablonu.";
            Accounts = TableData.Empty;
            return;
        }
        ChartInfo = $"{chart.Name}, wersja {chart.Version} — " + (chart.IsApproved ? $"zatwierdzony {chart.ApprovedOn.ToPl()}" : "NIEZATWIERDZONY (księgowanie zablokowane)");
        var list = await svc.GetAccountsAsync(chart.Id);
        if (!string.IsNullOrWhiteSpace(Filter))
            list = list.Where(a => a.Number.StartsWith(Filter.Trim(), StringComparison.OrdinalIgnoreCase) || a.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var t = new TableData(new TableColumn("Numer", Width: 0.8), new TableColumn("Nazwa", Width: 3), new TableColumn("Typ", Width: 1.2), new TableColumn("Bilans Wn", Width: 0.8),
            new TableColumn("Bilans Ma", Width: 0.8), new TableColumn("RZiS", Width: 0.6), new TableColumn("Rozrachunkowe", Width: 0.7), new TableColumn("CIT", Width: 1.1), new TableColumn("Aktywne", Width: 0.5));
        foreach (var a in list)
            t.Add(a.Id, a.Number, new string(' ', a.Level * 2) + a.Name, a.Type, a.BalanceDebitLine, a.BalanceCreditLine, a.ProfitLossLine, a.IsSettlementAccount, a.CitClassification, a.IsActive);
        Accounts = t;
    }

    [RelayCommand] private Task ApplyFilterAsync() => LoadAsync();

    partial void OnSelectedChanged(TableRow? value)
    {
        if (value?.Tag is not Guid id) return;
        _ = RunAsync(async () =>
        {
            await using var db = Session.OpenDb();
            var a = await db.Accounts.AsNoTracking().FirstAsync(x => x.Id == id);
            Number = a.Number; Name = a.Name; Type = Types.First(x => x.Value == a.Type);
            BalanceDebitLine = a.BalanceDebitLine ?? ""; BalanceCreditLine = a.BalanceCreditLine ?? ""; ProfitLossLine = a.ProfitLossLine ?? "";
            Settlement = a.IsSettlementAccount; IsActive = a.IsActive; Cit = CitClasses.First(x => x.Value == a.CitClassification);
        });
    }

    private Guid ChartId => _chartId ?? throw new DomainException("Brak planu kont.");

    [RelayCommand]
    private Task CreateFromTemplateAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new ChartService(db, Session.User, Session.Clock).CreateFromTemplateAsync(Session.CurrentYear.Id,
            ChartTemplate.Build(Session.Company!.LegalForm, Options.ToOptions()));
        await Options.ApplyPostingSettingsAsync(db, Session.CompanyId, Session.Clock);
    }, "Utworzono plan kont z kreatora — przejrzyj go i zatwierdź.");

    /// <summary>Dodanie szczegółowych kont kosztów do istniejącego planu (gdy konta 401/402 nie mają jeszcze zapisów).</summary>
    [RelayCommand]
    private Task AddDetailedCostAccountsAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var svc = new ChartService(db, Session.User, Session.Clock);
        var existing = (await svc.GetAccountsAsync(ChartId)).Select(a => a.Number).ToHashSet();
        var added = 0;
        foreach (var t in ChartTemplate.Build(Session.Company!.LegalForm, new ChartOptions(DetailedCosts: true)).Where(t => t.Number.Contains('-') && !existing.Contains(t.Number)
                     && (t.Number.StartsWith("401-") || t.Number.StartsWith("402-") || t.Number == "409-05")))
        {
            await svc.AddAccountAsync(ChartId, t.Number, t.Name, t.Type, t.BalanceDebitLine, t.BalanceCreditLine, t.ProfitLossLine, t.Settlement, t.Cit);
            added++;
        }
        await new ChartOptionsForm { DetailedCosts = true }.ApplyPostingSettingsAsync(db, Session.CompanyId, Session.Clock);
        StatusMessage = $"Dodano {added} kont kosztów.";
    }, "Dodano szczegółowe konta kosztów — kategorie wydatków będą trafiać na właściwe konta.");

    [RelayCommand]
    private Task AddAccountAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new ChartService(db, Session.User, Session.Clock).AddAccountAsync(ChartId, Number, Name, Type.Value, Parse.Trim(BalanceDebitLine),
            Parse.Trim(BalanceCreditLine), Parse.Trim(ProfitLossLine), Settlement, Cit.Value);
    }, "Dodano konto.");

    [RelayCommand]
    private Task UpdateAccountAsync() => ChangeAsync(async () =>
    {
        if (Selected?.Tag is not Guid id) throw new DomainException("Zaznacz konto na liście.");
        await using var db = Session.OpenDb();
        await new ChartService(db, Session.User, Session.Clock).UpdateAccountAsync(id, Name, Parse.Trim(BalanceDebitLine), Parse.Trim(BalanceCreditLine),
            Parse.Trim(ProfitLossLine), Cit.Value, IsActive);
    }, "Zapisano zmiany konta.");

    [RelayCommand]
    private Task DeleteAccountAsync() => ChangeAsync(async () =>
    {
        if (Selected?.Tag is not Guid id) throw new DomainException("Zaznacz konto na liście.");
        if (!await Dialogs.ConfirmAsync("Plan kont", $"Usunąć konto {Selected.Cells[0]}? Konto z zapisami nie może zostać usunięte.")) return;
        await using var db = Session.OpenDb();
        await new ChartService(db, Session.User, Session.Clock).DeleteAccountAsync(id);
    }, "Usunięto konto.");

    [RelayCommand]
    private Task ValidateAsync() => RunAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var v = await new ChartService(db, Session.User, Session.Clock).ValidateChartAsync(ChartId);
        await Dialogs.ShowTextAsync("Kontrola planu kont", v.IsValid && v.Warnings.Count == 0 ? "Plan kont nie zawiera błędów." : v.ToString());
    });

    [RelayCommand]
    private Task ApproveAsync() => ChangeAsync(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Plan kont", "Zatwierdzić plan kont? Plan kont jest elementem polityki rachunkowości (art. 10 ust. 1 pkt 3 UoR).")) return;
        await using var db = Session.OpenDb();
        await new SetupService(db, Session.Clock).ApproveChartAsync(Session.User, ChartId);
    }, "Plan kont zatwierdzony.");

    [RelayCommand]
    private Task ExportCsvAsync() => RunAsync(async () =>
    {
        var target = await Dialogs.SaveFileAsync("Eksport planu kont", $"plan-kont-{Session.CurrentYear.Name}.csv", new FileFilter("CSV", "*.csv"));
        if (target is null) return;
        await using var db = Session.OpenDb();
        await File.WriteAllTextAsync(target, await new ChartService(db, Session.User, Session.Clock).ExportCsvAsync(ChartId), new System.Text.UTF8Encoding(true));
        StatusMessage = $"Zapisano {target}";
    });

    [RelayCommand]
    private Task ImportCsvAsync() => ChangeAsync(async () =>
    {
        var file = await Dialogs.OpenFileAsync("Import kont z CSV (format jak w eksporcie)", new FileFilter("CSV", "*.csv"));
        if (file is null) return;
        var templates = ChartService.ParseCsv(await File.ReadAllTextAsync(file));
        await using var db = Session.OpenDb();
        var svc = new ChartService(db, Session.User, Session.Clock);
        var existing = (await svc.GetAccountsAsync(ChartId)).Select(a => a.Number).ToHashSet();
        var added = 0;
        foreach (var t in templates.Where(t => !existing.Contains(t.Number)).OrderBy(t => t.Number))
        {
            await svc.AddAccountAsync(ChartId, t.Number, t.Name, t.Type, t.BalanceDebitLine, t.BalanceCreditLine, t.ProfitLossLine, t.Settlement, t.Cit);
            added++;
        }
        StatusMessage = $"Dodano {added} kont (istniejące pominięto).";
    }, "Zaimportowano konta.");
}
