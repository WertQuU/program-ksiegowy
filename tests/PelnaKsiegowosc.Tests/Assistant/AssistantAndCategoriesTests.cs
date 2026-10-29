using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Assistant;

public class AssistantAndCategoriesTests
{
    private static async Task<Document> FuelPurchaseAsync(TestBooks t, string category = "PALIWO_OSOBOWY", decimal net = 1000m)
    {
        var cp = await t.AddCounterpartyAsync("D001", "Stacja paliw sp. z o.o.", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var accounts = await t.Db.Accounts.AsNoTracking().Where(a => a.ChartId == t.Chart.Id).ToListAsync();
        var cat = ExpenseCategories.Find(category)!;
        var d = new DateOnly(2026, 1, 10);
        var inv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cp.Id, d, d,
            new[] { new DocumentLineInput("Benzyna", 1, net, "23", "l", ExpenseCategories.ResolveAccount(cat, accounts), null, cat.Code, cat.NkupPercent) },
            ExternalNumber: "PAL/1/2026", ReceiptDate: d));
        Assert.True(await docs.ApplyCategoryDeductionAsync(inv.Id));
        await docs.ApproveAsync(inv.Id);
        await docs.PostAsync(inv.Id);
        return inv;
    }

    [Fact]
    public async Task Fuel_for_mixed_use_car_deducts_half_vat_and_marks_quarter_of_cost_as_nkup()
    {
        await using var t = await TestBooks.CreateAsync();
        var inv = await FuelPurchaseAsync(t);
        var doc = await t.Db.Documents.Include(x => x.VatLines).AsNoTracking().FirstAsync(x => x.Id == inv.Id);
        Assert.Equal(115m, doc.VatLines.Single().DeductibleVatPln);
        Assert.Equal(115m, await t.BalanceAsync("221-02"));
        Assert.Equal(1115m, await t.BalanceAsync("401"));
        var nkupLines = await t.Db.JournalLines.AsNoTracking().Where(l => l.CitClassification == CitClassification.KosztNiepodatkowy).ToListAsync();
        Assert.Equal(278.75m, nkupLines.Sum(l => l.Debit));

        var rec = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 1);
        Assert.Equal(836.25m, rec.TaxCosts);
        Assert.Contains(rec.Adjustments, a => a.Amount == 278.75m && a.Item.Contains("NKUP"));

        // storno odwraca także część NKUP
        var entryId = (await t.Db.Documents.AsNoTracking().FirstAsync(x => x.Id == inv.Id)).JournalEntryId!.Value;
        await new LedgerService(t.Db, t.User, t.Clock).ReverseAsync(entryId, "test", new DateOnly(2026, 1, 20));
        var rec2 = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 1);
        Assert.Equal(0m, rec2.TaxCosts);
    }

    [Fact]
    public async Task Representation_is_fully_nkup_and_hotel_vat_is_not_deductible()
    {
        await using var t = await TestBooks.CreateAsync();
        var hotel = ExpenseCategories.Find("PODROZ_HOTEL")!;
        Assert.Equal(0m, hotel.VatDeductionPercent);
        Assert.Equal(100m, ExpenseCategories.Find("REPREZENTACJA")!.NkupPercent);
        await FuelPurchaseAsync(t, "REPREZENTACJA", 200m);
        var rec = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 1);
        Assert.Equal(0m, rec.TaxCosts);
        Assert.Equal(0m, await t.BalanceAsync("221-02"));
    }

    [Fact]
    public void Every_category_has_valid_rate_and_percentages()
    {
        var rates = PelnaKsiegowosc.Domain.Vat.VatRules.Rates.Select(r => r.Code).ToHashSet();
        foreach (var c in ExpenseCategories.All)
        {
            Assert.Contains(c.DefaultVatRate, rates);
            Assert.InRange(c.VatDeductionPercent, 0, 100);
            Assert.InRange(c.NkupPercent, 0, 100);
            Assert.NotEmpty(c.Accounts);
            Assert.False(string.IsNullOrWhiteSpace(c.Hint));
        }
        Assert.Equal(ExpenseCategories.All.Count, ExpenseCategories.All.Select(c => c.Code).Distinct().Count());
    }

    [Fact]
    public async Task Assistant_detects_unposted_documents_unpaid_payables_and_missing_backup()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 2);
        var draft = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cp.Id, d, d,
            new[] { new DocumentLineInput("Usługa", 1, 100m, "23") }, ExternalNumber: "X/1", ReceiptDate: d));
        t.Clock.UtcNow = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
        var issues = await new BookkeepingAssistant(t.Db, t.User, t.Clock).CheckAsync(t.Year.Id, null);
        Assert.Contains(issues, i => i.Code == "DOK_ROBOCZE");
        Assert.Contains(issues, i => i.Code == "ZAKUP_ODLICZENIE" && i.ObjectId == draft.Id);
        Assert.Contains(issues, i => i.Code == "KOPIA");

        // zatwierdzona i zaksięgowana faktura niezapłacona > 90 dni po terminie → korekta VAT (art. 89b)
        await docs.SetVatDeductionAsync(draft.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100m, false) }, new DateOnly(2026, 1, 1));
        t.Clock.UtcNow = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await docs.ApproveAsync(draft.Id);
        await docs.PostAsync(draft.Id);
        t.Clock.UtcNow = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
        issues = await new BookkeepingAssistant(t.Db, t.User, t.Clock).CheckAsync(t.Year.Id, t.Clock.UtcNow);
        Assert.Contains(issues, i => i.Code == "ZOBOWIAZANIE_90" && i.Severity == IssueSeverity.Blad);
        Assert.DoesNotContain(issues, i => i.Code == "KOPIA");
    }

    [Fact]
    public async Task Assistant_flags_negative_cash_and_lists_vat_and_cit_deadlines()
    {
        await using var t = await TestBooks.CreateAsync();
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 5), "PK/1",
            new LineInput("402", 50m, 0), new LineInput("100", 0, 50m)));
        await t.Db.SaveChangesAsync();
        var a = new BookkeepingAssistant(t.Db, t.User, t.Clock);
        var issues = await a.CheckAsync(t.Year.Id, t.Clock.UtcNow);
        Assert.Contains(issues, i => i.Code == "KASA_UJEMNA" && i.Severity == IssueSeverity.Blad);
        var deadlines = await a.DeadlinesAsync();
        // VAT za 12/2025 nie dotyczy (poza rokiem); za 01/2026 — 25.02.2026 (środa)
        Assert.Contains(deadlines, x => x.Title.Contains("JPK_V7M") && x.Title.Contains("01.2026") && x.Date == new DateOnly(2026, 2, 25));
        Assert.Contains(deadlines, x => x.Title.Contains("Zaliczka CIT") && x.Date == new DateOnly(2026, 2, 20));
        Assert.Contains(deadlines, x => x.Title.Contains("Zaksięgowanie operacji") && x.Date == new DateOnly(2026, 2, 15));
    }
}
