using System.Text;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Application.Capital;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class SalesAndBankScenarioTests
{
    internal static async Task<Document> IssueAndPostSalesInvoiceAsync(TestBooks t, Guid cpId, decimal net, DateOnly date, string? name = null)
    {
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var inv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, cpId, date, date,
            new[] { new DocumentLineInput(name ?? "Usługa programistyczna", 1, net, "23", "usł.") }, DueDate: date.AddDays(14)));
        await docs.ApproveAsync(inv.Id);
        await docs.PostAsync(inv.Id);
        return await docs.GetAsync(inv.Id);
    }

    [Fact]
    public async Task Sales_invoice_posting_creates_balanced_entry_vat_register_and_receivable()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Klient sp. z o.o.", "7740001454");
        var inv = await IssueAndPostSalesInvoiceAsync(t, cp.Id, 1000m, new DateOnly(2026, 1, 12));

        Assert.Equal(DocumentStatus.Zaksiegowany, inv.Status);
        Assert.StartsWith("FS/1/01/2026", inv.Number);
        Assert.Equal(1230m, inv.GrossAmountPln);
        var entry = await t.Db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == inv.JournalEntryId);
        Assert.Equal("SPR", entry.JournalCode);
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);
        Assert.Equal(1230m, entry.Lines.Single(l => l.AccountNumber == "200").Debit);
        Assert.Equal(1000m, entry.Lines.Single(l => l.AccountNumber == "700-01").Credit);
        Assert.Equal(230m, entry.Lines.Single(l => l.AccountNumber == "221-01").Credit);

        var reg = await t.Db.VatRegister.SingleAsync();
        Assert.Equal(VatRegisterType.Sprzedaz, reg.Register);
        Assert.Equal(new DateOnly(2026, 1, 1), reg.Period);
        Assert.Equal(("K_19", "K_20"), (reg.JpkNetField, reg.JpkTaxField));
        Assert.Equal(1000m, reg.NetPln);
        Assert.Equal(230m, reg.VatPln);
        Assert.Equal(JpkKsefMarker.BFK, reg.KsefMarker);

        var item = await t.Db.OpenItems.SingleAsync();
        Assert.Equal(new DateOnly(2026, 1, 26), item.DueDate);
        Assert.Equal(inv.Id, item.DocumentId);
        Assert.Equal(PaymentStatus.Nieoplacony, inv.PaymentStatus);
        Assert.True(await t.Db.DocumentHistory.AnyAsync(h => h.DocumentId == inv.Id && h.Action == "ZAKSIEGOWANY"));
    }

    [Fact]
    public async Task Posted_document_cannot_be_edited_and_requires_correction()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Klient");
        var inv = await IssueAndPostSalesInvoiceAsync(t, cp.Id, 100m, new DateOnly(2026, 1, 12));
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var ex = await Assert.ThrowsAsync<DomainException>(() => docs.UpdateInvoiceAsync(inv.Id, new InvoiceInput(DocumentKind.FakturaSprzedazy, cp.Id,
            inv.IssueDate, inv.IssueDate, new[] { new DocumentLineInput("X", 1, 200m, "23") })));
        Assert.Contains("korygując", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => docs.CancelAsync(inv.Id, "pomyłka"));
    }

    [Fact]
    public async Task Bank_csv_import_auto_matches_partial_and_full_payments_and_blocks_duplicates()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Klient sp. z o.o.", "7740001454");
        var inv1 = await IssueAndPostSalesInvoiceAsync(t, cp.Id, 1000m, new DateOnly(2026, 1, 12));
        var inv2 = await IssueAndPostSalesInvoiceAsync(t, cp.Id, 500m, new DateOnly(2026, 1, 13));
        var bank = new BankService(t.Db, t.User, t.Clock);
        var acc = await bank.CreateMoneyAccountAsync(MoneyAccountKind.RachunekBankowy, "mBank PLN", "130-01", "PL61109010140000071219812874");

        var csv = $"""
            Data;Kwota;Kontrahent;Rachunek;Tytul;Ref
            2026-01-20;615,00;Klient sp. z o.o.;;Zaplata {inv2.Number};R1
            2026-01-21;500,00;Klient sp. z o.o.;;Czesciowa zaplata {inv1.Number};R2
            2026-01-22;-12,50;Bank;;Oplata za prowadzenie rachunku;R3
            """;
        var bytes = Encoding.UTF8.GetBytes(csv);
        var parsed = CsvStatementParser.Parse(bytes, new CsvImportProfile());
        var result = await bank.ImportStatementAsync(acc.Id, parsed, "wyciag.csv", bytes);
        Assert.Equal(3, result.Imported);
        await Assert.ThrowsAsync<DomainException>(() => bank.ImportStatementAsync(acc.Id, parsed, "wyciag.csv", bytes));

        var matched = await bank.AutoMatchAsync(result.StatementId);
        Assert.Equal(1, matched); // pełna zapłata FS 2 — jednoznaczna
        var txs = await t.Db.MoneyTransactions.OrderBy(x => x.BookingDate).ToListAsync();
        var full = txs[0];
        var partial = txs[1];
        var fee = txs[2];
        Assert.Equal(TransactionStatus.Dopasowana, full.Status);

        var sugg = await bank.SuggestMatchesAsync(partial.Id);
        var item1 = await t.Db.OpenItems.SingleAsync(o => o.DocumentId == inv1.Id);
        Assert.Equal(item1.Id, sugg[0].OpenItemId);
        await bank.MatchAsync(partial.Id, new[] { new Allocation(item1.Id, 500m) }, null);
        await bank.MatchAsync(fee.Id, Array.Empty<Allocation>(), "751-03");

        foreach (var tx in txs) await bank.PostTransactionAsync(tx.Id);
        var d1 = await t.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == inv1.Id);
        var d2 = await t.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == inv2.Id);
        Assert.Equal(PaymentStatus.CzesciowoOplacony, d1.PaymentStatus);
        Assert.Equal(PaymentStatus.Oplacony, d2.PaymentStatus);
        Assert.Equal(1102.50m, await t.BalanceAsync("130-01"));
        Assert.Equal(730m, await t.BalanceAsync("200"));
        Assert.Equal(12.50m, await t.BalanceAsync("751-03"));
        Assert.Equal(StatementStatus.Rozliczony, (await t.Db.MoneyStatements.SingleAsync()).Status);
        await Assert.ThrowsAsync<DomainException>(() => bank.PostTransactionAsync(full.Id));

        // ponowny import pliku z jedną nową operacją: znane operacje pominięte jako duplikaty
        var csv2 = csv + "\n2026-01-23;100,00;Klient;;Zaliczka;R4";
        var b2 = Encoding.UTF8.GetBytes(csv2);
        var r2 = await bank.ImportStatementAsync(acc.Id, CsvStatementParser.Parse(b2, new CsvImportProfile()), "wyciag2.csv", b2);
        Assert.Equal(1, r2.Imported);
        Assert.Equal(3, r2.Duplicates);

        var recon = await new BankReconciliation(t.Db).ReconcileAsync(t.Company.Id, t.Year.Id, new DateOnly(2026, 1, 31));
        Assert.Equal(1102.50m, recon.Single().LedgerBalance);
        Assert.Equal(1, recon.Single().UnpostedTransactions);
        Assert.Empty(await new IntegrityService(t.Db, t.Clock).CheckOpenItemsAgainstLedgerAsync(t.Year.Id, t.Year.EndDate));
    }

    [Fact]
    public async Task Mt940_statement_is_parsed()
    {
        var mt = """
            :20:STMT0001
            :25:/PL61109010140000071219812874
            :28C:00012/001
            :60F:C260131PLN1000,00
            :61:2602010201C1230,00NTRFNONREF//BREF1
            :86:020~00TRF~20Zaplata FS/1/01/2026~21 za uslugi~32KLIENT SP Z O O~38PL27114020040000300201355387
            :61:2602020202D50,00NTRFNONREF
            :86:Oplata bankowa
            :62F:C260202PLN2180,00
            -
            """;
        var s = Mt940Parser.Parse(Encoding.UTF8.GetBytes(mt));
        Assert.Equal("00012/001", s.Number);
        Assert.Equal(1000m, s.OpeningBalance);
        Assert.Equal(2180m, s.ClosingBalance);
        Assert.Equal(2, s.Transactions.Count);
        Assert.Equal(1230m, s.Transactions[0].Amount);
        Assert.Equal("Zaplata FS/1/01/2026 za uslugi", s.Transactions[0].Title);
        Assert.Equal("KLIENT SP Z O O", s.Transactions[0].CounterpartyName);
        Assert.Equal(-50m, s.Transactions[1].Amount);
        Assert.Equal(new DateOnly(2026, 2, 1), s.Transactions[0].BookingDate);
    }

    [Fact]
    public async Task Cash_cannot_go_negative()
    {
        await using var t = await TestBooks.CreateAsync();
        var bank = new BankService(t.Db, t.User, t.Clock);
        var cash = await bank.CreateMoneyAccountAsync(MoneyAccountKind.Kasa, "Kasa główna", "100", null);
        var kp = await bank.AddCashOperationAsync(cash.Id, new DateOnly(2026, 1, 5), 300m, "Wpłata do kasy z banku", null);
        await bank.MatchAsync(kp.Id, Array.Empty<Allocation>(), "139");
        await bank.PostTransactionAsync(kp.Id);
        var kw = await bank.AddCashOperationAsync(cash.Id, new DateOnly(2026, 1, 6), -400m, "Zakup materiałów", null);
        await bank.MatchAsync(kw.Id, Array.Empty<Allocation>(), "401");
        var ex = await Assert.ThrowsAsync<DomainException>(() => bank.PostTransactionAsync(kw.Id));
        Assert.Contains("ujemne", ex.Message);
        Assert.Equal(300m, await t.BalanceAsync("100"));
    }

    [Fact]
    public async Task Sales_correction_reduces_receivable_and_register()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Klient");
        var inv = await IssueAndPostSalesInvoiceAsync(t, cp.Id, 1000m, new DateOnly(2026, 1, 12));
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var kor = await docs.CreateCorrectionAsync(inv.Id, "Rabat posprzedażowy 10%", new DateOnly(2026, 2, 3),
            new[] { new DocumentLineInput("Rabat do usługi programistycznej", 1, -100m, "23") });
        Assert.Equal(-123m, kor.GrossAmountPln);
        await docs.ApproveAsync(kor.Id);
        await docs.PostAsync(kor.Id);
        Assert.Equal(1107m, await t.BalanceAsync("200"));
        Assert.Equal(-900m, await t.BalanceAsync("700-01"));
        var item = await t.Db.OpenItems.AsNoTracking().SingleAsync(o => o.DocumentId == inv.Id);
        Assert.Equal(1107m, item.RemainingPln);
        var regFeb = await t.Db.VatRegister.SingleAsync(v => v.Period == new DateOnly(2026, 2, 1));
        Assert.True(regFeb.IsCorrection);
        Assert.Equal(-100m, regFeb.NetPln);
        Assert.Equal(-23m, regFeb.VatPln);
        Assert.Empty(await new IntegrityService(t.Db, t.Clock).CheckOpenItemsAgainstLedgerAsync(t.Year.Id, t.Year.EndDate));
    }

    [Fact]
    public async Task Compensation_settles_receivable_and_payable()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Kontrahent", "7740001454");
        var inv = await IssueAndPostSalesInvoiceAsync(t, cp.Id, 1000m, new DateOnly(2026, 1, 12));
        var purchase = await PurchaseScenarioTests.RegisterPurchaseAsync(t, cp.Id, 400m, "FV/9/2026", 100m);
        var settlements = new SettlementService(t.Db, t.User, t.Clock);
        var rec = await t.Db.OpenItems.SingleAsync(o => o.Side == OpenItemSide.Naleznosc);
        var pay = await t.Db.OpenItems.SingleAsync(o => o.Side == OpenItemSide.Zobowiazanie);
        await settlements.CompensateAsync(rec.Id, pay.Id, 492m, new DateOnly(2026, 1, 31), "NK/1/2026");
        Assert.Equal(738m, await t.BalanceAsync("200"));
        Assert.Equal(0m, await t.BalanceAsync("210"));
        var aging = await settlements.AgingAsync(OpenItemSide.Naleznosc, new DateOnly(2026, 3, 1));
        Assert.Single(aging);
        Assert.Equal("31–60 dni", aging[0].Bucket);
        Assert.Equal(PaymentStatus.Oplacony, (await t.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == purchase.Id)).PaymentStatus);
    }
}

public class PurchaseScenarioTests
{
    internal static async Task<Document> RegisterPurchaseAsync(TestBooks t, Guid cpId, decimal net, string number, decimal deductionPercent,
        string? costAccount = null, VatTransactionType type = VatTransactionType.Krajowa, string rate = "23")
    {
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 15);
        var doc = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cpId, d, d,
            new[] { new DocumentLineInput("Usługa księgowa", 1, net, rate, AccountNumber: costAccount) }, ExternalNumber: number, ReceiptDate: d, VatTransactionType: type));
        if (doc.VatAmountPln != 0)
            await docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { [rate] = (deductionPercent, false) }, new DateOnly(2026, 1, 1));
        await docs.ApproveAsync(doc.Id);
        await docs.PostAsync(doc.Id);
        return await docs.GetAsync(doc.Id);
    }

    [Fact]
    public async Task Purchase_requires_explicit_vat_deduction_decision()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 15);
        var doc = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cp.Id, d, d, new[] { new DocumentLineInput("Kolacja z klientem — restauracja", 1, 100m, "8") },
            ExternalNumber: "R/1", ReceiptDate: d));
        var ex = await Assert.ThrowsAsync<DomainException>(() => docs.ApproveAsync(doc.Id));
        Assert.Contains("odliczenia VAT", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["8"] = (0, false) }, new DateOnly(2025, 12, 1)));
        await Assert.ThrowsAsync<DomainException>(() => docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["8"] = (0, false) }, new DateOnly(2026, 5, 1)));
        await docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["8"] = (0, false) }, new DateOnly(2026, 1, 1));
        var v = await docs.ValidateAsync(await docs.GetAsync(doc.Id));
        Assert.Contains(v.Warnings, w => w.Contains("gastronomiczne"));
        await docs.ApproveAsync(doc.Id);
        await docs.PostAsync(doc.Id);
        Assert.Equal(108m, await t.BalanceAsync("402")); // VAT nieodliczalny w koszcie
        Assert.Equal(0m, await t.BalanceAsync("221-02"));
        var reg = await t.Db.VatRegister.SingleAsync();
        Assert.Equal(0m, reg.DeductibleVatPln);
        // duplikat numeru dostawcy
        var dup = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cp.Id, d, d, new[] { new DocumentLineInput("X", 1, 1m, "23") }, ExternalNumber: "R/1", ReceiptDate: d));
        await docs.SetVatDeductionAsync(dup.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100, false) }, new DateOnly(2026, 1, 1));
        var dupEx = await Assert.ThrowsAsync<DomainException>(() => docs.ApproveAsync(dup.Id));
        Assert.Contains("duplikat", dupEx.Message);
    }

    [Fact]
    public async Task Half_deduction_posts_non_deductible_vat_to_cost()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Leasing sp. z o.o.", "7740001454");
        await RegisterPurchaseAsync(t, cp.Id, 1000m, "L/1", 50m, "409-04");
        Assert.Equal(1115m, await t.BalanceAsync("409-04"));
        Assert.Equal(115m, await t.BalanceAsync("221-02"));
        Assert.Equal(-1230m, await t.BalanceAsync("210"));
    }

    [Fact]
    public async Task Import_of_services_is_self_assessed_in_both_registers()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = t.Db.Counterparties.Add(new Domain.Counterparties.Counterparty { CompanyId = t.Company.Id, Code = "F001", Name = "Cloud Inc.", CountryCode = "IE", VatEuNumber = "IE1234567X" }).Entity;
        await t.Db.SaveChangesAsync();
        var doc = await RegisterPurchaseAsync(t, cp.Id, 1000m, "INV-77", 100m, type: VatTransactionType.ImportUslug28b);
        Assert.Equal(1000m, doc.GrossAmountPln);
        Assert.Equal(-1000m, await t.BalanceAsync("210"));
        Assert.Equal(230m, await t.BalanceAsync("221-02"));
        Assert.Equal(-230m, await t.BalanceAsync("221-01"));
        var sales = await t.Db.VatRegister.SingleAsync(v => v.Register == VatRegisterType.Sprzedaz);
        Assert.Equal(("K_29", "K_30"), (sales.JpkNetField, sales.JpkTaxField));
        var purch = await t.Db.VatRegister.SingleAsync(v => v.Register == VatRegisterType.Zakup);
        Assert.Equal(230m, purch.DeductibleVatPln);

        var decl = await new VatService(t.Db, t.User, t.Clock).ComputeDeclarationAsync(new DateOnly(2026, 1, 1));
        Assert.Equal(230m, decl.Fields["P_30"]);
        Assert.Equal(230m, decl.Fields["P_43"]);
        Assert.Equal(0m, decl.Fields["P_51"]);
    }

    [Fact]
    public async Task Vat_declaration_and_settlement_entry_with_rounding()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Klient", "7740001454");
        await SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, cp.Id, 1000.40m, new DateOnly(2026, 1, 12)); // VAT 230,09
        await RegisterPurchaseAsync(t, cp.Id, 200m, "Z/1", 100m); // VAT 46,00
        var vat = new VatService(t.Db, t.User, t.Clock);
        var d = await vat.ComputeDeclarationAsync(new DateOnly(2026, 1, 1));
        Assert.Equal(1000m, d.Fields["P_19"]);
        Assert.Equal(230m, d.Fields["P_20"]);
        Assert.Equal(1000m, d.Fields["P_37"]);
        Assert.Equal(230m, d.Fields["P_38"]);
        Assert.Equal(46m, d.Fields["P_43"]);
        Assert.Equal(184m, d.Fields["P_51"]);
        Assert.Equal(1000.40m, d.RegisterTotals["K_19"]);
        var entry = await vat.PostSettlementAsync(new DateOnly(2026, 1, 1));
        Assert.Equal(0m, await t.BalanceAsync("221-01"));
        Assert.Equal(0m, await t.BalanceAsync("221-02"));
        Assert.Equal(-184m, await t.BalanceAsync("221-04"));
        Assert.Equal(-0.09m, await t.BalanceAsync("760-03"));
        await Assert.ThrowsAsync<DomainException>(() => vat.PostSettlementAsync(new DateOnly(2026, 1, 1)));
    }
}

public class FixedAssetCitCapitalTests
{
    [Fact]
    public async Task Depreciation_run_posts_book_and_records_tax_and_cit_reconciles_difference()
    {
        await using var t = await TestBooks.CreateAsync();
        var fa = new FixedAssetService(t.Db, t.User, t.Clock);
        var asset = await fa.RegisterAsync(new FixedAsset
        {
            InventoryNumber = "ST/2026/1", Name = "Serwer", Category = FixedAssetCategory.SrodekTrwaly, KstSymbol = "491",
            AcquisitionDate = new DateOnly(2026, 1, 10), PlacedInServiceDate = new DateOnly(2026, 1, 10),
            InitialValueBook = 24000m, InitialValueTax = 24000m, BookRatePercent = 20m, TaxRatePercent = 30m,
            AssetAccountNumber = "010-03", AccumulatedDepreciationAccountNumber = "070-03", ExpenseAccountNumber = "400"
        }, "080");
        Assert.Equal(24000m, await t.BalanceAsync("010-03"));
        await Assert.ThrowsAsync<DomainException>(() => fa.RunDepreciationAsync(2026, 1).ContinueWith(_ => fa.RunDepreciationAsync(2026, 1)).Unwrap());
        var feb = await fa.RunDepreciationAsync(2026, 2);
        Assert.Equal(400m, feb.TotalBook);
        Assert.Equal(600m, feb.TotalTax);
        await Assert.ThrowsAsync<DomainException>(() => fa.RunDepreciationAsync(2026, 2));
        Assert.Equal(400m, await t.BalanceAsync("400"));
        Assert.Equal(-400m, await t.BalanceAsync("070-03"));

        var cp = await t.AddCounterpartyAsync("K001", "Klient", "7740001454");
        await SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, cp.Id, 10000m, new DateOnly(2026, 2, 10));
        var rep = await t.Db.Accounts.FirstAsync(a => a.Number == "409-02");
        var e = await t.Ledger().CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 2, 15), "PK/REP", new("409-02", 300m, 0), new("130-01", 0, 300m)));
        await t.Ledger().PostAsync(e.Id);

        var cit = new CitService(t.Db, t.User, t.Clock);
        var rec = await cit.ReconcileAsync(t.Year.Id, 2);
        Assert.Equal(10000m, rec.TaxRevenue);
        Assert.Equal(600m, rec.TaxCosts); // amortyzacja podatkowa zamiast bilansowej, reprezentacja NKUP
        Assert.Equal(9300m, rec.BookResultBeforeTax);
        Assert.Contains(rec.Adjustments, a => a.Item.Contains("NKUP") && a.Amount == 300m);
        Assert.Contains(rec.Adjustments, a => a.Item.Contains("amortyzacji") && a.Amount == -200m);
        Assert.Equal(9400m, rec.Computation.TaxBase);
        Assert.Equal(9m, rec.Computation.RatePercent); // pierwszy rok działalności
        Assert.Equal(846m, rec.Computation.Tax);
        var adv = await cit.ComputeAdvanceAsync(t.Year.Id, 2);
        Assert.Equal(846m, adv.AdvanceDue);
        Assert.Equal(new DateOnly(2026, 3, 20), adv.PaymentDeadline);
        await cit.ApproveAdvanceAsync(adv.Id, true);
        await Assert.ThrowsAsync<DomainException>(() => cit.ComputeAdvanceAsync(t.Year.Id, 2));
    }

    [Fact]
    public async Task Disposal_requires_depreciation_and_derecognises_asset()
    {
        await using var t = await TestBooks.CreateAsync();
        var fa = new FixedAssetService(t.Db, t.User, t.Clock);
        var asset = await fa.RegisterAsync(new FixedAsset
        {
            InventoryNumber = "ST/1", Name = "Laptop", AcquisitionDate = new DateOnly(2026, 1, 5), PlacedInServiceDate = new DateOnly(2026, 1, 5),
            InitialValueBook = 12000m, InitialValueTax = 12000m, BookRatePercent = 30m, TaxRatePercent = 30m,
            AssetAccountNumber = "010-03", AccumulatedDepreciationAccountNumber = "070-03"
        }, "080");
        await fa.RunDepreciationAsync(2026, 2);
        await Assert.ThrowsAsync<DomainException>(() => fa.DisposeAsync(asset.Id, new DateOnly(2026, 3, 20), false, "Kradzież — protokół LT/1"));
        await fa.RunDepreciationAsync(2026, 3);
        await fa.DisposeAsync(asset.Id, new DateOnly(2026, 3, 20), false, "Kradzież — protokół LT/1");
        Assert.Equal(0m, await t.BalanceAsync("010-03"));
        Assert.Equal(0m, await t.BalanceAsync("070-03"));
        Assert.Equal(11400m, await t.BalanceAsync("761-01"));
        Assert.Equal(FixedAssetStatus.Zlikwidowany, (await t.Db.FixedAssets.SingleAsync()).Status);
        var apr = await fa.RunDepreciationAsync(2026, 4);
        Assert.Equal(0m, apr.TotalBook);
    }

    [Fact]
    public async Task Capital_events_require_documents_and_solvency_test()
    {
        await using var t = await TestBooks.CreateAsync();
        var cap = new CapitalService(t.Db, t.User, t.Clock);
        var sh = await cap.AddShareholderAsync("Jan Akcjonariusz", null, false);
        var subscribe = await cap.CreateEventAsync(new CapitalEvent
        {
            Type = CapitalEventType.ObjecieAkcjiWkladPieniezny, EventDate = new DateOnly(2026, 1, 2), ShareholderId = sh.Id,
            ShareCount = 1000, ShareCapitalAmount = 1000m, SupplementaryCapitalAmount = 9000m, ContributionType = ContributionType.Pieniezny,
            Description = "Objęcie akcji serii A"
        });
        await Assert.ThrowsAsync<DomainException>(() => cap.PostEventAsync(subscribe.Id)); // dokumenty niepotwierdzone
        var missing = await Assert.ThrowsAsync<DomainException>(() => cap.ConfirmDocumentsAsync(subscribe.Id));
        Assert.Contains("umowa", missing.Message);
        subscribe.AgreementReference = "Umowa spółki z 02.01.2026, Rep. A 1/2026";
        await t.Db.SaveChangesAsync();
        await cap.ConfirmDocumentsAsync(subscribe.Id);
        await cap.PostEventAsync(subscribe.Id);
        Assert.Equal(-1000m, await t.BalanceAsync("801"));
        Assert.Equal(-9000m, await t.BalanceAsync("802"));
        Assert.Equal(10000m, await t.BalanceAsync("245-01"));

        var pay = await cap.CreateEventAsync(new CapitalEvent { Type = CapitalEventType.WplataWkladu, EventDate = new DateOnly(2026, 1, 5), ShareholderId = sh.Id, TotalAmount = 10000m });
        await cap.ConfirmDocumentsAsync(pay.Id);
        await cap.PostEventAsync(pay.Id);
        Assert.Equal(0m, await t.BalanceAsync("245-01"));
        Assert.Equal(10000m, await t.BalanceAsync("130-01"));

        var advance = await cap.CreateEventAsync(new CapitalEvent
        {
            Type = CapitalEventType.ZaliczkaNaPoczetDywidendy, EventDate = new DateOnly(2026, 1, 20), ShareholderId = sh.Id, TotalAmount = 500m,
            ResolutionReference = "Uchwała zarządu 1/2026"
        });
        var ex = await Assert.ThrowsAsync<DomainException>(() => cap.ConfirmDocumentsAsync(advance.Id));
        Assert.Contains("wypłacalności", ex.Message);
        advance.SolvencyTestConfirmed = true;
        await t.Db.SaveChangesAsync();
        await cap.ConfirmDocumentsAsync(advance.Id);
        await cap.PostEventAsync(advance.Id);
        Assert.Equal(500m, await t.BalanceAsync("821"));
        await Assert.ThrowsAsync<DomainException>(() => cap.CreateEventAsync(new CapitalEvent { Type = CapitalEventType.ObjecieAkcjiWkladPieniezny, EventDate = new DateOnly(2026, 1, 2), ShareCapitalAmount = 1, ContributionType = ContributionType.PracaLubUslugi }));
    }
}

public class AttachmentTests
{
    [Fact]
    public async Task Attachments_are_validated_hashed_and_verified_on_read()
    {
        await using var t = await TestBooks.CreateAsync();
        var store = new LocalFileStore(Path.Combine(t.Directory, "zalaczniki"));
        var cp = await t.AddCounterpartyAsync("K001", "Klient");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 10);
        var inv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, cp.Id, d, d, new[] { new DocumentLineInput("X", 1, 1m, "23") }));
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%test\n");
        var att = await docs.AddAttachmentAsync(inv.Id, store, "faktura.pdf", new MemoryStream(pdf));
        Assert.Equal(64, att.Sha256.Length);
        Assert.Equal(pdf, await docs.ReadAttachmentAsync(att.Id, store));
        await Assert.ThrowsAsync<DomainException>(() => docs.AddAttachmentAsync(inv.Id, store, "wirus.exe", new MemoryStream(new byte[] { 0x4D, 0x5A, 1 })));
        await Assert.ThrowsAsync<DomainException>(() => docs.AddAttachmentAsync(inv.Id, store, "udaje.pdf", new MemoryStream(new byte[] { 0x4D, 0x5A, 1, 2 })));
        await Assert.ThrowsAsync<DomainException>(() => docs.AddAttachmentAsync(inv.Id, store, "pusty.pdf", new MemoryStream()));
        File.WriteAllBytes(Path.Combine(store.RootPath, att.StoragePath), Encoding.ASCII.GetBytes("%PDF-zmieniony"));
        var ex = await Assert.ThrowsAsync<DomainException>(() => docs.ReadAttachmentAsync(att.Id, store));
        Assert.Contains("SHA-256", ex.Message);
    }
}
