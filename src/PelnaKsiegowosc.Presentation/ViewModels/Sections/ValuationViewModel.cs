using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Odpisy aktualizujące należności (wiekowanie, spisanie) i rezerwy na zobowiązania (w tym rezerwa urlopowa).</summary>
public sealed partial class ValuationViewModel : ViewModelBase
{
    private readonly AppSession _session;

    public ValuationViewModel(AppSession session, IDialogService dialogs) : base(dialogs)
    {
        _session = session;
        _asOf = session.Clock.Today.ToPl();
        _date = session.Clock.Today.ToPl();
        _kind = Kinds[2];
    }

    public List<Choice<ProvisionKind>> Kinds { get; } = new()
    {
        new(ProvisionKind.Urlopowa, "Urlopowa (843 / 404)"),
        new(ProvisionKind.Emerytalna, "Odprawy emerytalne, nagrody jubileuszowe (843 / 405)"),
        new(ProvisionKind.NaKoszty, "Na koszty okresu, np. badanie sprawozdania (840 / 402)"),
        new(ProvisionKind.Gwarancyjna, "Gwarancyjna (840 / 761-03)"),
        new(ProvisionKind.Inna, "Inna: sprawy sądowe, kary (840 / 761-03)"),
    };

    [ObservableProperty] private string _asOf;
    [ObservableProperty] private string _policyText = "";
    [ObservableProperty] private TableData _proposals = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedProposal;
    [ObservableProperty] private TableData _provisions = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedProvision;
    [ObservableProperty] private Choice<ProvisionKind> _kind;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _amount = "";
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _costAccount = "";

    private ValuationService Service(Data.KsiegiDbContext db) => new(db, _session.User, _session.Clock);

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(async () =>
    {
        await using var db = _session.OpenDb();
        var svc = Service(db);
        var p = await svc.PolicyAsync();
        PolicyText = $"Polityka: odpis {p.PercentPartial:0.##}% po {p.DaysPartial} dniach zwłoki, {p.PercentFull:0.##}% po {p.DaysFull} dniach. Odpis jest kosztem niepodatkowym do czasu uprawdopodobnienia nieściągalności (art. 16 ust. 1 pkt 26a ustawy o CIT).";
        var t = new TableData(new TableColumn("Kontrahent", Width: 1.6), new TableColumn("Dokument", Width: 1.2), new TableColumn("Termin", ColumnKind.Date),
            new TableColumn("Dni zwłoki", ColumnKind.Integer, 0.6), new TableColumn("Do zapłaty", ColumnKind.Money), new TableColumn("Odpis %", ColumnKind.Money, 0.6),
            new TableColumn("Wymagany odpis", ColumnKind.Money), new TableColumn("Dotychczas", ColumnKind.Money), new TableColumn("Do zaksięgowania", ColumnKind.Money));
        foreach (var r in await svc.ProposeAsync(Parse.Date(AsOf, "Na dzień")))
            t.Add(r.OpenItemId, r.Counterparty, r.Reference, r.DueDate, r.DaysOverdue, r.Remaining, r.Percent, r.Required, r.Existing, r.Change);
        Proposals = t;
        var pt = new TableData(new TableColumn("Data", ColumnKind.Date), new TableColumn("Rodzaj"), new TableColumn("Opis", Width: 2), new TableColumn("Konto", Width: 0.5),
            new TableColumn("Kwota", ColumnKind.Money), new TableColumn("Wykorzystano", ColumnKind.Money), new TableColumn("Rozwiązano", ColumnKind.Money), new TableColumn("Pozostało", ColumnKind.Money));
        foreach (var r in await svc.ProvisionsAsync())
            pt.Add(r.Id, r.Date, Display.Enum(r.Kind), r.Description, r.AccountNumber, r.Amount, r.Used, r.Released, r.Remaining);
        Provisions = pt;
    });

    [RelayCommand]
    private async Task PostAllowancesAsync()
    {
        if (!await Dialogs.ConfirmAsync("Odpisy aktualizujące", $"Zaksięgować wszystkie propozycje odpisów i rozwiązań na dzień {AsOf}?")) return;
        var n = 0;
        if (await RunAsync(async () =>
            {
                await using var db = _session.OpenDb();
                n = await Service(db).PostProposalsAsync(Parse.Date(AsOf, "Na dzień"));
            }))
        {
            await RefreshAsync();
            StatusMessage = $"Zaksięgowano zmian odpisów: {n}.";
        }
    }

    [RelayCommand]
    private async Task WriteOffAsync()
    {
        if (SelectedProposal?.Tag is not Guid id) { await Dialogs.ErrorAsync("Spisanie należności", "Zaznacz należność na liście."); return; }
        var reason = await Dialogs.PromptAsync("Spisanie należności",
            "Spisanie rozlicza należność w całości (wykorzystanie odpisu, reszta w pozostałe koszty operacyjne). Podaj podstawę (np. postanowienie komornika o nieściągalności, przedawnienie):");
        if (string.IsNullOrWhiteSpace(reason)) return;
        if (await RunAsync(async () =>
            {
                await using var db = _session.OpenDb();
                await Service(db).WriteOffAsync(id, Parse.Date(AsOf, "Na dzień"), reason);
            }, "Należność spisana."))
            await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetPolicyAsync()
    {
        var text = await Dialogs.PromptAsync("Progi odpisów", "Podaj: dni;procent;dni;procent (np. 180;50;365;100) — zgodnie z polityką rachunkowości:", "180;50;365;100");
        if (string.IsNullOrWhiteSpace(text)) return;
        if (await RunAsync(async () =>
            {
                var p = text.Split(';', StringSplitOptions.TrimEntries);
                if (p.Length != 4) throw new DomainException("Podaj cztery wartości oddzielone średnikami.");
                await using var db = _session.OpenDb();
                await Service(db).SetPolicyAsync(new AllowancePolicy(Parse.Int(p[0], "Dni"), Parse.Amount(p[1], "Procent"), Parse.Int(p[2], "Dni"), Parse.Amount(p[3], "Procent")));
            }, "Zapisano progi odpisów."))
            await RefreshAsync();
    }

    [RelayCommand]
    private async Task CreateProvisionAsync()
    {
        if (await RunAsync(async () =>
            {
                await using var db = _session.OpenDb();
                await Service(db).CreateProvisionAsync(Kind.Value, Description, Parse.Amount(Amount, "Kwota"), Parse.Date(Date, "Data"), Parse.Trim(CostAccount));
                Description = "";
                Amount = "";
            }, "Rezerwa utworzona i zaksięgowana."))
            await RefreshAsync();
    }

    [RelayCommand] private Task UseProvisionAsync() => MoveProvisionAsync(false);
    [RelayCommand] private Task ReleaseProvisionAsync() => MoveProvisionAsync(true);

    private async Task MoveProvisionAsync(bool release)
    {
        if (SelectedProvision?.Tag is not Guid id) { await Dialogs.ErrorAsync("Rezerwy", "Zaznacz rezerwę na liście."); return; }
        var text = await Dialogs.PromptAsync(release ? "Rozwiązanie rezerwy" : "Wykorzystanie rezerwy",
            release ? "Kwota rozwiązywanej (zbędnej) rezerwy:" : "Kwota wykorzystana (rzeczywisty koszt ujęto już w księgach, np. fakturą):", SelectedProvision.Cells[^1]?.ToString());
        if (string.IsNullOrWhiteSpace(text)) return;
        if (await RunAsync(async () =>
            {
                await using var db = _session.OpenDb();
                await Service(db).UseProvisionAsync(id, Parse.Amount(text, "Kwota"), Parse.Date(Date, "Data"), release);
            }, release ? "Rezerwa rozwiązana." : "Rezerwa wykorzystana."))
            await RefreshAsync();
    }

    [RelayCommand]
    private async Task HolidayAsync()
    {
        // ewidencja urlopów (Płace → Kadry) — gdy jest prowadzona, rezerwa liczy się bez pytań
        List<HolidayProvisionRow> fromLeave = new();
        await RunAsync(async () =>
        {
            await using var db = _session.OpenDb();
            if (await db.Absences.AnyAsync(a => a.CompanyId == _session.CompanyId))
                fromLeave = await Service(db).HolidayProvisionFromLeaveAsync(Parse.Date(Date, "Data").Year);
        });
        if (fromLeave.Count > 0)
        {
            var total = fromLeave.Sum(r => r.Amount);
            Kind = Kinds[0];
            Description = $"Niewykorzystane urlopy na {Parse.Date(Date, "Data").ToPl()} (ewidencja urlopów)";
            Amount = Money.Format(total);
            await Dialogs.ShowTextAsync("Rezerwa urlopowa — z ewidencji urlopów",
                string.Join(Environment.NewLine, fromLeave.Select(r => $"{r.Employee}: {r.UnusedDays:0.##} dni × {Money.Format(r.DailyRate)} = {Money.Format(r.Amount)}")) +
                $"{Environment.NewLine}{Environment.NewLine}Razem: {Money.Format(total)} zł. Kwotę wpisano w formularz — sprawdź i kliknij „Utwórz rezerwę”.");
            return;
        }
        var employees = new List<(Guid Id, string Name)>();
        if (!await RunAsync(async () =>
            {
                await using var db = _session.OpenDb();
                employees = (await db.Employees.AsNoTracking().Where(e => e.CompanyId == _session.CompanyId && e.IsActive && e.ContractType == Domain.Payroll.ContractType.UmowaOPrace)
                    .OrderBy(e => e.LastName).ToListAsync()).Select(e => (e.Id, $"{e.LastName} {e.FirstName}")).ToList();
                if (employees.Count == 0) throw new DomainException("Brak pracowników na umowę o pracę (sekcja Płace).");
            })) return;
        var days = new Dictionary<Guid, decimal>();
        foreach (var (id, name) in employees)
        {
            var d = await Dialogs.PromptAsync("Rezerwa urlopowa", $"{name}: liczba niewykorzystanych dni urlopu na dzień {Date} (z ewidencji urlopów):", "0");
            if (d is null) return;
            if (Money.TryParse(d, out var v) && v > 0) days[id] = v;
        }
        await RunAsync(async () =>
        {
            await using var db = _session.OpenDb();
            var date = Parse.Date(Date, "Data");
            var rows = await Service(db).HolidayProvisionAsync(days, date.Year);
            var total = rows.Sum(r => r.Amount);
            if (total == 0) throw new DomainException("Brak niewykorzystanych urlopów — rezerwa nie jest potrzebna.");
            var text = string.Join(Environment.NewLine, rows.Select(r => $"{r.Employee}: {r.UnusedDays:0.##} dni × {Money.Format(r.DailyRate)} = {Money.Format(r.Amount)}"));
            Kind = Kinds[0];
            Description = $"Niewykorzystane urlopy na {date.ToPl()}";
            Amount = Money.Format(total);
            await Dialogs.ShowTextAsync("Rezerwa urlopowa — wyliczenie",
                text + $"{Environment.NewLine}{Environment.NewLine}Razem: {Money.Format(total)} zł (wynagrodzenie ze składkami pracodawcy / współczynnik urlopowy). " +
                "Kwotę wpisano w formularz — sprawdź i kliknij „Utwórz rezerwę”. Na koniec kolejnego okresu rozwiąż starą rezerwę i utwórz nową.");
        });
    }
}
