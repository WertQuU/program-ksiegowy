using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Presentation.ViewModels.Sections;

/// <summary>Rachunki bankowe i kasy: import wyciągów (MT940, CSV), dopasowanie do rozrachunków, księgowanie, operacje kasowe.</summary>
public sealed partial class BankViewModel : SectionViewModel
{
    public BankViewModel(AppSession s, IDialogService d) : base(s, d, "Bank i kasa", "🏦") { }

    [ObservableProperty] private List<Choice<Guid>> _moneyAccounts = new();
    [ObservableProperty] private Choice<Guid>? _moneyAccount;
    [ObservableProperty] private TableData _transactions = TableData.Empty;
    [ObservableProperty] private TableRow? _selected;
    [ObservableProperty] private bool _onlyOpen = true;
    [ObservableProperty] private TableData _suggestions = TableData.Empty;
    [ObservableProperty] private TableRow? _selectedSuggestion;
    [ObservableProperty] private string _offsetAccount = "";
    [ObservableProperty] private string _exchangeRate = "";

    [ObservableProperty] private Choice<MoneyAccountKind> _newKind = Choices.Of<MoneyAccountKind>()[0];
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newLedgerAccount = "130-02";
    [ObservableProperty] private string _newIban = "";
    [ObservableProperty] private string _newCurrency = "PLN";
    public List<Choice<MoneyAccountKind>> Kinds { get; } = Choices.Of<MoneyAccountKind>();

    [ObservableProperty] private string _cashDate = "";
    [ObservableProperty] private string _cashAmount = "";
    [ObservableProperty] private string _cashTitle = "";
    [ObservableProperty] private List<CounterpartyChoice> _counterparties = new();
    [ObservableProperty] private CounterpartyChoice? _cashCounterparty;

    [ObservableProperty] private string _csvSeparator = ";";
    [ObservableProperty] private string _csvDateFormat = "yyyy-MM-dd";
    [ObservableProperty] private string _csvColumns = "0;1;2;3;4;5";

    partial void OnMoneyAccountChanged(Choice<Guid>? value) => _ = RunAsync(LoadTransactionsAsync);
    partial void OnOnlyOpenChanged(bool value) => _ = RunAsync(LoadTransactionsAsync);

    private Guid AccountId => MoneyAccount?.Value ?? throw new DomainException("Wybierz rachunek bankowy lub kasę.");
    private Guid SelectedId => Selected?.Tag is Guid g ? g : throw new DomainException("Zaznacz operację na liście.");

    protected override async Task LoadCoreAsync()
    {
        await using var db = Session.OpenDb();
        var accounts = await db.MoneyAccounts.AsNoTracking().Where(m => m.CompanyId == Session.CompanyId && m.IsActive).OrderBy(m => m.Kind).ThenBy(m => m.Name).ToListAsync();
        var keep = MoneyAccount?.Value;
        MoneyAccounts = accounts.Select(a => new Choice<Guid>(a.Id, $"{(a.Kind == MoneyAccountKind.Kasa ? "Kasa" : "Bank")}: {a.Name} ({a.LedgerAccountNumber}, {a.Currency})")).ToList();
        MoneyAccount = MoneyAccounts.FirstOrDefault(a => a.Value == keep) ?? MoneyAccounts.FirstOrDefault();
        Counterparties = await CounterpartyChoice.LoadAsync(db, Session.CompanyId);
        if (string.IsNullOrEmpty(CashDate)) CashDate = Session.Clock.Today.ToPl();
        await LoadTransactionsAsync();
    }

    private async Task LoadTransactionsAsync()
    {
        Suggestions = TableData.Empty;
        if (MoneyAccount is null) { Transactions = TableData.Empty; return; }
        await using var db = Session.OpenDb();
        var q = db.MoneyTransactions.AsNoTracking().Where(t => t.MoneyAccountId == MoneyAccount.Value);
        if (OnlyOpen) q = q.Where(t => t.Status == TransactionStatus.Nierozliczona || t.Status == TransactionStatus.Dopasowana);
        var list = await q.OrderByDescending(t => t.BookingDate).Take(5000).ToListAsync();
        var t = new TableData(new TableColumn("Data", ColumnKind.Date, 0.8), new TableColumn("Kwota", ColumnKind.Money, 0.9), new TableColumn("Waluta", Width: 0.4),
            new TableColumn("Kontrahent", Width: 1.8), new TableColumn("Tytuł", Width: 2.8), new TableColumn("Referencja", Width: 1), new TableColumn("Status", Width: 0.9));
        foreach (var x in list) t.Add(x.Id, x.BookingDate, x.Amount, x.Currency, x.CounterpartyName, x.Title, x.BankReference, x.Status);
        Transactions = t;
    }

    partial void OnSelectedChanged(TableRow? value)
    {
        if (value?.Tag is not Guid id) { Suggestions = TableData.Empty; return; }
        _ = RunAsync(async () =>
        {
            await using var db = Session.OpenDb();
            var list = await new BankService(db, Session.User, Session.Clock).SuggestMatchesAsync(id);
            var t = new TableData(new TableColumn("Rozrachunek", Width: 1.4), new TableColumn("Kontrahent", Width: 1.8), new TableColumn("Pozostało", ColumnKind.Money),
                new TableColumn("Trafność", ColumnKind.Integer, 0.5), new TableColumn("Uzasadnienie", Width: 2));
            foreach (var s in list) t.Add(s, s.Reference, s.CounterpartyName, s.Remaining, s.Score, s.Reason);
            Suggestions = t;
        });
    }

    [RelayCommand]
    private Task CreateAccountAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new BankService(db, Session.User, Session.Clock).CreateMoneyAccountAsync(NewKind.Value, NewName, NewLedgerAccount.Trim(), Parse.Trim(NewIban), NewCurrency.Trim().ToUpperInvariant());
        NewName = NewIban = "";
    }, "Utworzono rachunek/kasę.");

    [RelayCommand]
    private Task ImportMt940Async() => ImportAsync(mt940: true);

    [RelayCommand]
    private Task ImportCsvAsync() => ImportAsync(mt940: false);

    private Task ImportAsync(bool mt940) => ChangeAsync(async () =>
    {
        var id = AccountId;
        var file = await Dialogs.OpenFileAsync(mt940 ? "Wyciąg MT940" : "Wyciąg CSV",
            mt940 ? new FileFilter("MT940", "*.sta", "*.mt940", "*.txt", "*.940") : new FileFilter("CSV", "*.csv", "*.txt"), new FileFilter("Wszystkie pliki", "*"));
        if (file is null) return;
        var bytes = await File.ReadAllBytesAsync(file);
        ParsedStatement parsed;
        if (mt940) parsed = Mt940Parser.Parse(bytes);
        else
        {
            var cols = CsvColumns.Split(';', ',').Select(c => Parse.Int(c, "Kolumny CSV")).ToArray();
            if (cols.Length < 5) throw new DomainException("Podaj numery kolumn: data;kwota;kontrahent;rachunek kontrahenta;tytuł[;referencja] (od 0).");
            parsed = CsvStatementParser.Parse(bytes, new CsvImportProfile
            {
                Separator = string.IsNullOrEmpty(CsvSeparator) ? ';' : CsvSeparator[0], DateFormat = CsvDateFormat,
                BookingDateColumn = cols[0], AmountColumn = cols[1], CounterpartyNameColumn = cols[2], CounterpartyIbanColumn = cols[3], TitleColumn = cols[4],
                ReferenceColumn = cols.Length > 5 ? cols[5] : null
            });
        }
        await using var db = Session.OpenDb();
        var svc = new BankService(db, Session.User, Session.Clock);
        var r = await svc.ImportStatementAsync(id, parsed, Path.GetFileName(file), bytes);
        var matched = await svc.AutoMatchAsync(r.StatementId);
        await Dialogs.InfoAsync("Import wyciągu", $"Zaimportowano operacji: {r.Imported}, pominięto duplikaty: {r.Duplicates}, dopasowano automatycznie: {matched}." +
            (r.Warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, r.Warnings) : ""));
    }, "Zaimportowano wyciąg.");

    [RelayCommand]
    private Task MatchSuggestionAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        if (SelectedSuggestion?.Tag is not MatchSuggestion s) throw new DomainException("Zaznacz propozycję rozrachunku.");
        await using var db = Session.OpenDb();
        var tr = await db.MoneyTransactions.AsNoTracking().FirstAsync(t => t.Id == id);
        var amount = Math.Min(Math.Abs(tr.Amount), s.Remaining);
        await new BankService(db, Session.User, Session.Clock).MatchAsync(id, new[] { new Allocation(s.OpenItemId, amount) },
            Math.Abs(tr.Amount) > amount ? Parse.Trim(OffsetAccount) ?? throw new DomainException($"Kwota operacji przekracza rozrachunek o {Money.Format(Math.Abs(tr.Amount) - amount)} — wskaż konto przeciwstawne dla nadwyżki.") : null);
    }, "Dopasowano operację do rozrachunku.");

    [RelayCommand]
    private Task MatchToAccountAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        var account = Parse.Trim(OffsetAccount) ?? throw new DomainException("Podaj konto przeciwstawne (np. 751-03 dla prowizji bankowych).");
        await using var db = Session.OpenDb();
        await new BankService(db, Session.User, Session.Clock).MatchAsync(id, Array.Empty<Allocation>(), account);
    }, "Przypisano konto przeciwstawne.");

    [RelayCommand]
    private Task PostAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        await using var db = Session.OpenDb();
        var e = await new BankService(db, Session.User, Session.Clock).PostTransactionAsync(id, Parse.OptionalAmount(ExchangeRate, "Kurs"));
        StatusMessage = $"Zaksięgowano {e.JournalNumber}";
    }, "Operacja zaksięgowana.");

    [RelayCommand]
    private Task PostAllMatchedAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        var ids = await db.MoneyTransactions.Where(t => t.MoneyAccountId == AccountId && t.Status == TransactionStatus.Dopasowana).OrderBy(t => t.BookingDate).Select(t => t.Id).ToListAsync();
        if (ids.Count == 0) throw new DomainException("Brak dopasowanych operacji do zaksięgowania.");
        if (!await Dialogs.ConfirmAsync("Księgowanie", $"Zaksięgować {ids.Count} dopasowanych operacji?")) return;
        var errors = new StringBuilder();
        var ok = 0;
        foreach (var id in ids)
        {
            await using var one = Session.OpenDb();
            try { await new BankService(one, Session.User, Session.Clock).PostTransactionAsync(id); ok++; }
            catch (DomainException ex) { errors.AppendLine(ex.Message); }
        }
        await Dialogs.InfoAsync("Księgowanie operacji", $"Zaksięgowano: {ok} z {ids.Count}." + (errors.Length > 0 ? Environment.NewLine + errors : ""));
    }, "Zakończono księgowanie operacji.");

    [RelayCommand]
    private Task SkipAsync() => ChangeAsync(async () =>
    {
        var id = SelectedId;
        var reason = await Dialogs.PromptAsync("Pominięcie operacji", "Podaj przyczynę pominięcia (np. operacja zaksięgowana innym dokumentem):");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await using var db = Session.OpenDb();
        await new BankService(db, Session.User, Session.Clock).SkipAsync(id, reason.Trim());
    }, "Operację pominięto.");

    [RelayCommand]
    private Task AddCashOperationAsync() => ChangeAsync(async () =>
    {
        await using var db = Session.OpenDb();
        await new BankService(db, Session.User, Session.Clock).AddCashOperationAsync(AccountId, Parse.Date(CashDate, "Data"),
            Parse.Amount(CashAmount, "Kwota (dodatnia = KP, ujemna = KW)"), CashTitle, CashCounterparty?.Id);
        CashAmount = CashTitle = "";
    }, "Dodano operację kasową — dopasuj ją i zaksięguj.");
}
