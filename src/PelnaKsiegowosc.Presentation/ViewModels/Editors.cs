using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Vat;

namespace PelnaKsiegowosc.Presentation.ViewModels;

/// <summary>Kontrahent na liście wyboru.</summary>
public sealed record CounterpartyChoice(Guid Id, string Code, string Name, string? Nip)
{
    public string Label => $"{Code} — {Name}" + (string.IsNullOrEmpty(Nip) ? "" : $" (NIP {Nip})");
    public override string ToString() => Label;

    public static async Task<List<CounterpartyChoice>> LoadAsync(KsiegiDbContext db, Guid companyId) =>
        (await db.Counterparties.AsNoTracking().Where(c => c.CompanyId == companyId && c.IsActive).OrderBy(c => c.Code).ToListAsync())
        .Select(c => new CounterpartyChoice(c.Id, c.Code, c.Name, c.Nip)).ToList();
}

/// <summary>Pozycja faktury w edytorze (pola tekstowe parsowane przy zapisie).</summary>
public sealed partial class InvoiceLineRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _quantity = "1";
    [ObservableProperty] private string _unit = "szt.";
    [ObservableProperty] private string _unitNetPrice = "";
    [ObservableProperty] private string _vatRate = "23";
    [ObservableProperty] private string? _accountNumber;
    [ObservableProperty] private string? _gtu;

    public static IReadOnlyList<string> RateCodes { get; } = VatRules.Rates.Select(r => r.Code).Distinct().ToList();

    public DocumentLineInput ToInput(int no) => new(
        string.IsNullOrWhiteSpace(Name) ? throw new DomainException($"Pozycja {no}: podaj nazwę towaru lub usługi.") : Name.Trim(),
        Parse.Amount(Quantity, $"Ilość (pozycja {no})"), Parse.Amount(UnitNetPrice, $"Cena netto (pozycja {no})"),
        VatRate, string.IsNullOrWhiteSpace(Unit) ? "szt." : Unit.Trim(), Parse.Trim(AccountNumber), Parse.Trim(Gtu));
}

/// <summary>Edytor faktury sprzedaży lub zakupu (nowej albo roboczej).</summary>
public sealed partial class InvoiceEditor : ObservableObject
{
    public InvoiceEditor(bool purchase) => IsPurchase = purchase;

    public bool IsPurchase { get; }
    public bool IsSales => !IsPurchase;

    /// <summary>Identyfikator edytowanego dokumentu roboczego (null = nowy).</summary>
    [ObservableProperty] private Guid? _documentId;
    [ObservableProperty] private string _header = "Nowy dokument";
    [ObservableProperty] private List<CounterpartyChoice> _counterparties = new();
    [ObservableProperty] private CounterpartyChoice? _counterparty;
    [ObservableProperty] private Choice<DocumentKind> _kind = null!;
    [ObservableProperty] private string _issueDate = "";
    [ObservableProperty] private string _operationDate = "";
    [ObservableProperty] private string _dueDate = "";
    [ObservableProperty] private string _receiptDate = "";
    [ObservableProperty] private string _externalNumber = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _currency = "PLN";
    [ObservableProperty] private string _exchangeRate = "1";
    [ObservableProperty] private string _exchangeRateTable = "";
    [ObservableProperty] private string _exchangeRateDate = "";
    [ObservableProperty] private Choice<PaymentMethod> _paymentMethod = Choices.Of<PaymentMethod>()[0];
    [ObservableProperty] private Choice<VatTransactionType> _transactionType = Choices.Of<VatTransactionType>()[0];
    [ObservableProperty] private bool _splitPayment;
    [ObservableProperty] private string _jpkProcedures = "";
    [ObservableProperty] private string _jpkDocumentType = "";
    [ObservableProperty] private InvoiceLineRow? _selectedLine;

    public ObservableCollection<InvoiceLineRow> Lines { get; } = new();
    public List<Choice<PaymentMethod>> PaymentMethods { get; } = Choices.Of<PaymentMethod>();
    public List<Choice<VatTransactionType>> TransactionTypes { get; } = Choices.Of<VatTransactionType>();
    public List<Choice<DocumentKind>> Kinds { get; private set; } = new();
    public IReadOnlyList<string> RateCodes => InvoiceLineRow.RateCodes;

    public void Reset(DateOnly today, List<CounterpartyChoice> counterparties)
    {
        DocumentId = null;
        Header = IsPurchase ? "Nowa faktura zakupu" : "Nowa faktura sprzedaży";
        Kinds = IsPurchase
            ? new() { new(DocumentKind.FakturaZakupu, "Faktura zakupu"), new(DocumentKind.Rachunek, "Rachunek (bez VAT)") }
            : new() { new(DocumentKind.FakturaSprzedazy, "Faktura sprzedaży") };
        OnPropertyChanged(nameof(Kinds));
        Kind = Kinds[0];
        Counterparties = counterparties;
        Counterparty = null;
        IssueDate = OperationDate = today.ToPl();
        DueDate = today.AddDays(14).ToPl();
        ReceiptDate = IsPurchase ? today.ToPl() : "";
        ExternalNumber = Description = JpkProcedures = JpkDocumentType = ExchangeRateTable = ExchangeRateDate = "";
        Currency = "PLN";
        ExchangeRate = "1";
        PaymentMethod = PaymentMethods[0];
        TransactionType = TransactionTypes[0];
        SplitPayment = false;
        Lines.Clear();
        Lines.Add(new InvoiceLineRow());
    }

    public void Load(Document d, List<CounterpartyChoice> counterparties)
    {
        Reset(d.IssueDate, counterparties);
        DocumentId = d.Id;
        Header = $"Edycja dokumentu roboczego {d.Number}";
        Kind = Kinds.FirstOrDefault(k => k.Value == d.Kind) ?? Kinds[0];
        Counterparty = counterparties.FirstOrDefault(c => c.Id == d.CounterpartyId);
        IssueDate = d.IssueDate.ToPl();
        OperationDate = d.OperationDate.ToPl();
        DueDate = d.DueDate.ToPl();
        ReceiptDate = d.ReceiptDate.ToPl();
        ExternalNumber = d.ExternalNumber ?? "";
        Description = d.Description;
        Currency = d.Currency;
        ExchangeRate = d.ExchangeRate.ToString("0.####", Money.Pl);
        ExchangeRateTable = d.ExchangeRateTable ?? "";
        ExchangeRateDate = d.ExchangeRateDate.ToPl();
        PaymentMethod = PaymentMethods.First(p => p.Value == d.PaymentMethod);
        TransactionType = TransactionTypes.First(t => t.Value == d.VatTransactionType);
        SplitPayment = d.SplitPayment;
        JpkProcedures = d.JpkProcedures ?? "";
        JpkDocumentType = d.JpkDocumentType ?? "";
        Lines.Clear();
        foreach (var l in d.Lines.OrderBy(l => l.LineNo))
            Lines.Add(new InvoiceLineRow
            {
                Name = l.Name, Quantity = l.Quantity.ToString("0.####", Money.Pl), Unit = l.Unit, UnitNetPrice = l.UnitNetPrice.ToString("0.00##", Money.Pl),
                VatRate = l.VatRateCode, Gtu = l.GtuCode
            });
    }

    [RelayCommand] private void AddLine() => Lines.Add(new InvoiceLineRow());

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine is not null) Lines.Remove(SelectedLine);
    }

    public IReadOnlyList<DocumentLineInput> LineInputs()
    {
        if (Lines.Count == 0) throw new DomainException("Dodaj co najmniej jedną pozycję.");
        return Lines.Select((l, i) => l.ToInput(i + 1)).ToList();
    }

    public InvoiceInput ToInput()
    {
        if (Counterparty is null) throw new DomainException("Wybierz kontrahenta (sekcja „Kontrahenci” pozwala dodać nowego).");
        var issue = Parse.Date(IssueDate, "Data wystawienia");
        var cur = string.IsNullOrWhiteSpace(Currency) ? "PLN" : Currency.Trim().ToUpperInvariant();
        var rate = cur == "PLN" ? 1m : Parse.Amount(ExchangeRate, "Kurs waluty");
        if (IsPurchase && string.IsNullOrWhiteSpace(ExternalNumber)) throw new DomainException("Podaj numer faktury dostawcy.");
        return new InvoiceInput(Kind.Value, Counterparty.Id, issue, Parse.OptionalDate(OperationDate, "Data sprzedaży/dostawy") ?? issue, LineInputs(),
            Description.Trim(), Parse.OptionalDate(DueDate, "Termin płatności"), cur, rate, Parse.Trim(ExchangeRateTable),
            Parse.OptionalDate(ExchangeRateDate, "Data kursu"), IsPurchase ? ExternalNumber.Trim() : null,
            IsPurchase ? Parse.OptionalDate(ReceiptDate, "Data wpływu") : null, PaymentMethod.Value, TransactionType.Value, SplitPayment,
            Parse.Trim(JpkProcedures), Parse.Trim(JpkDocumentType));
    }
}

/// <summary>Pozycja dekretu w edytorze (konto, Wn, Ma, kontrahent, opis).</summary>
public sealed partial class PostingLineRow : ObservableObject
{
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private string _debit = "";
    [ObservableProperty] private string _credit = "";
    [ObservableProperty] private CounterpartyChoice? _counterparty;
    [ObservableProperty] private string _description = "";
}

/// <summary>Edytor pozycji dekretu — polecenie księgowania, dokument wewnętrzny, zapis ręczny.</summary>
public sealed partial class PostingLinesEditor : ObservableObject
{
    public ObservableCollection<PostingLineRow> Lines { get; } = new();
    [ObservableProperty] private List<CounterpartyChoice> _counterparties = new();
    [ObservableProperty] private PostingLineRow? _selectedLine;
    [ObservableProperty] private string _totals = "";

    public void Reset(List<CounterpartyChoice> counterparties)
    {
        Counterparties = counterparties;
        Lines.Clear();
        Lines.Add(new PostingLineRow());
        Lines.Add(new PostingLineRow());
        Totals = "";
    }

    [RelayCommand] private void AddLine() => Lines.Add(new PostingLineRow());

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine is not null) Lines.Remove(SelectedLine);
    }

    [RelayCommand]
    private void Recalculate()
    {
        try
        {
            var l = ToInputs(requireBalance: false);
            var wn = l.Sum(x => x.Debit);
            var ma = l.Sum(x => x.Credit);
            Totals = $"Wn {Money.Format(wn)}   Ma {Money.Format(ma)}   różnica {Money.Format(wn - ma)}";
        }
        catch (DomainException ex) { Totals = ex.Message; }
    }

    public IReadOnlyList<LineInput> ToInputs(bool requireBalance = true)
    {
        var result = new List<LineInput>();
        var no = 0;
        foreach (var r in Lines)
        {
            no++;
            if (string.IsNullOrWhiteSpace(r.Account) && string.IsNullOrWhiteSpace(r.Debit) && string.IsNullOrWhiteSpace(r.Credit)) continue;
            if (string.IsNullOrWhiteSpace(r.Account)) throw new DomainException($"Pozycja {no}: podaj numer konta.");
            var wn = Parse.Amount(r.Debit, $"Wn (pozycja {no})");
            var ma = Parse.Amount(r.Credit, $"Ma (pozycja {no})");
            result.Add(new LineInput(r.Account.Trim(), wn, ma, r.Counterparty?.Id, Parse.Trim(r.Description)));
        }
        if (result.Count == 0) throw new DomainException("Dodaj pozycje dekretu.");
        if (requireBalance && result.Sum(x => x.Debit) != result.Sum(x => x.Credit))
            throw new DomainException($"Suma Wn ({Money.Format(result.Sum(x => x.Debit))}) różni się od sumy Ma ({Money.Format(result.Sum(x => x.Credit))}).");
        return result;
    }
}
