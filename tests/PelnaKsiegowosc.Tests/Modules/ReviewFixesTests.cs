using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Application.Capital;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Cit;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Jpk;
using PelnaKsiegowosc.Infrastructure.Xml;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Poprawki z przeglądu zgodności: stawka 9% CIT, art. 18f, JPK (K_11/K_12, MPP), harmonogram JPK_CIT.</summary>
public class ReviewFixesTests
{
    [Theory]
    [InlineData(2026, 1, 15, 2026, 2, 13, 1)]
    [InlineData(2026, 1, 15, 2026, 2, 12, 0)]
    [InlineData(2026, 1, 15, 2026, 3, 14, 2)]
    [InlineData(2026, 2, 1, 2026, 2, 28, 1)]
    [InlineData(2026, 2, 1, 2026, 4, 30, 3)]
    [InlineData(2024, 2, 1, 2024, 2, 29, 1)]
    [InlineData(2026, 1, 31, 2026, 3, 29, 2)]
    [InlineData(2026, 1, 1, 2026, 12, 31, 12)]
    public void Tax_year_months_preserve_calendar_months_and_count_remaining_thirty_days(int sy, int sm, int sd, int ey, int em, int ed, int expected)
        => Assert.Equal(expected, CitCalculator.FullMonths(new(sy, sm, sd), new(ey, em, ed)));

    [Fact]
    public async Task Manual_article18f_cannot_be_added_to_automatic_and_transition_does_not_double_count()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var doc = await PurchaseAsync(t, cp.Id, "FZ/18f", 1000m);
        var item = await t.Db.OpenItems.SingleAsync(o => o.DocumentId == doc.Id);
        var service = new CitService(t.Db, t.User, t.Clock);
        var manual = await service.AddAdjustmentAsync(t.Year.Id, 4, CitAdjustmentType.KosztNKUP, 1000m,
            "Nieuregulowane zobowiązanie", "art. 18f", openItemId: item.Id);
        var before = await service.ReconcileAsync(t.Year.Id, 4);
        Assert.Equal(0m, before.Operating.TaxCosts);
        Assert.Empty(before.LiabilityAdjustments);
        await Assert.ThrowsAsync<DomainException>(() => service.QualifyLiabilityAsync(item.Id, true, "Przejście na automat"));
        await t.ReopenAsync();
        service = new CitService(t.Db, t.User, t.Clock);
        Assert.Equal(item.Id, (await t.Db.CitAdjustments.SingleAsync()).OpenItemId);
        await service.RemoveManualAdjustmentAsync(manual.Id);
        await service.QualifyLiabilityAsync(item.Id, true, "Przejście po usunięciu korekty ręcznej");
        var after = await service.ReconcileAsync(t.Year.Id, 4);
        Assert.Equal(before.Operating.TaxCosts, after.Operating.TaxCosts);
        Assert.Single(after.LiabilityAdjustments);
        await Assert.ThrowsAsync<DomainException>(() => service.AddAdjustmentAsync(t.Year.Id, 4, CitAdjustmentType.KosztNKUP,
            1000m, "Korekta", "art. 18f", openItemId: item.Id));
        await Assert.ThrowsAsync<DomainException>(() => service.AddAdjustmentAsync(t.Year.Id, 4, CitAdjustmentType.KosztNKUP,
            1000m, "Korekta", "art. 18f"));
    }

    [Fact]
    public async Task Approved_manual_article18f_cannot_be_removed_or_relinked()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var doc = await PurchaseAsync(t, cp.Id, "FZ/18f", 1000m);
        var item = await t.Db.OpenItems.SingleAsync(o => o.DocumentId == doc.Id);
        var service = new CitService(t.Db, t.User, t.Clock);
        var manual = await service.AddAdjustmentAsync(t.Year.Id, 4, CitAdjustmentType.KosztNKUP, 1000m, "Korekta", "art. 18f", openItemId: item.Id);
        await service.SetLimitEurRateAsync(t.Year.StartDate, 4m);
        var advance = await service.ComputeAdvanceAsync(t.Year.Id, 4);
        await service.ApproveAdvanceAsync(advance.Id, true);
        await Assert.ThrowsAsync<DomainException>(() => service.RemoveManualAdjustmentAsync(manual.Id));
        await Assert.ThrowsAsync<DomainException>(() => service.LinkManualLiabilityAsync(manual.Id, item.Id));
        await Assert.ThrowsAsync<DomainException>(() => service.AddAdjustmentAsync(t.Year.Id, 4, CitAdjustmentType.KosztNKUP, 1m, "Nowa", null));
        Assert.Single(await t.Db.CitAdjustments.ToListAsync());
    }

    [Fact]
    public async Task Legacy_unlinked_article18f_must_be_linked_before_automatic_qualification()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var doc = await PurchaseAsync(t, cp.Id, "FZ/18f", 1000m);
        var item = await t.Db.OpenItems.SingleAsync(o => o.DocumentId == doc.Id);
        var old = new CitAdjustment { CompanyId = t.Company.Id, FiscalYearId = t.Year.Id, PeriodNumber = 4,
            Type = CitAdjustmentType.KosztNKUP, Amount = 1000m, Description = "Stara korekta", LegalBasis = "art. 18 f" };
        t.Db.CitAdjustments.Add(old);
        await t.Db.SaveChangesAsync();
        var service = new CitService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => service.QualifyLiabilityAsync(item.Id, true, "Kwalifikacja"));
        await service.LinkManualLiabilityAsync(old.Id, item.Id);
        Assert.Empty((await service.ReconcileAsync(t.Year.Id, 4)).LiabilityAdjustments);
    }

    [Fact]
    public async Task Capital_cost_nkup_does_not_reduce_operating_costs()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var account = await t.Db.Accounts.SingleAsync(a => a.Number == "751-03");
        account.CitClassification = CitClassification.KosztZyskiKapitalowe;
        await t.Db.SaveChangesAsync();
        await PurchaseAsync(t, cp.Id, "FZ/KAP", 1000m, account: "751-03", nkupPercent: 25m);
        var rec = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 1);
        Assert.Equal(750m, rec.CapitalGains.TaxCosts);
        Assert.Equal(0m, rec.Operating.TaxCosts);
    }

    [Fact]
    public async Task Small_taxpayer_limit_overrides_company_flag_and_ignores_drafts()
    {
        await using var t = await TestBooks.CreateAsync();
        t.Year.IsFirstYear = false;
        t.Company.IsSmallCitTaxpayer = true;
        t.Db.FiscalYears.Add(new Domain.Periods.FiscalYear { CompanyId = t.Company.Id, Name = "2025",
            StartDate = new(2025, 1, 1), EndDate = new(2025, 12, 31) });
        var document = new Document { CompanyId = t.Company.Id, Kind = DocumentKind.FakturaSprzedazy,
            Number = "FS/2025", IssueDate = new(2025, 12, 31), OperationDate = new(2025, 12, 31), GrossAmountPln = 9_000_000m };
        t.Db.Documents.Add(document);
        await t.Db.SaveChangesAsync();
        var service = new CitService(t.Db, t.User, t.Clock);
        await service.SetLimitEurRateAsync(t.Year.StartDate, 4m);
        await service.SetStatusEurRateAsync(2025, 4m);
        Assert.Equal(9m, (await service.ReconcileAsync(t.Year.Id, 1)).Rate.RatePercent);
        await Assert.ThrowsAsync<DomainException>(() => service.ComputeAdvanceAsync(t.Year.Id, 1));
        document.Status = DocumentStatus.Zaksiegowany;
        await t.Db.SaveChangesAsync();
        Assert.Equal(19m, (await service.ComputeAdvanceAsync(t.Year.Id, 1)).RatePercent);
        Assert.True(t.Company.IsSmallCitTaxpayer);
        await service.SetPreviousSalesAsync(t.Year.Id, 10_000_000m);
        Assert.Equal(19m, (await service.ComputeAdvanceAsync(t.Year.Id, 1)).RatePercent);
    }

    [Fact]
    public async Task Article18f_requires_qualification_and_explicit_tax_classification()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var document = await PurchaseAsync(t, cp.Id, "FZ/ANALIZA", 1000m);
        var service = new CitService(t.Db, t.User, t.Clock);
        Assert.Empty((await service.ReconcileAsync(t.Year.Id, 4)).LiabilityAdjustments);
        var item = await t.Db.OpenItems.SingleAsync(o => o.DocumentId == document.Id);
        await service.QualifyLiabilityAsync(item.Id, true, "Zweryfikowano warunki art. 18f");
        Assert.Single((await service.ReconcileAsync(t.Year.Id, 4)).LiabilityAdjustments);
        var account = await t.Db.Accounts.SingleAsync(a => a.Number == "402");
        account.CitClassification = CitClassification.DoAnalizy;
        await t.Db.SaveChangesAsync();
        var unresolved = await service.ReconcileAsync(t.Year.Id, 4);
        Assert.Empty(unresolved.LiabilityAdjustments);
        Assert.Contains(unresolved.Warnings, w => w.Contains("nierozstrzygniętej"));
    }

    [Fact]
    public async Task Partial_legacy_psa_correction_transfers_unpaid_contribution_and_accepts_next_payment()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new CapitalService(t.Db, t.User, t.Clock);
        var shareholder = await service.AddShareholderAsync("Akcjonariusz", null, false);
        var cp = shareholder.CounterpartyId!.Value;
        var date = new DateOnly(2026, 1, 2);
        var subscription = await service.CreateEventAsync(new CapitalEvent
        {
            Type = CapitalEventType.ObjecieAkcjiWkladPieniezny, EventDate = date,
            ShareholderId = shareholder.Id, ShareCapitalAmount = 5000m, AgreementReference = "Umowa"
        });
        var original = await t.Ledger().CreateAndPostAsync(TestBooks.Entry(date, "OLD", new("245-01", 5000m, 0, cp), new("801", 0, 5000m)));
        subscription.JournalEntryId = original.Id;
        subscription.Status = CapitalEventStatus.Zaksiegowany;
        await t.Db.SaveChangesAsync();
        async Task Pay(decimal amount, int day)
        {
            var payment = await service.CreateEventAsync(new CapitalEvent { Type = CapitalEventType.WplataWkladu,
                ShareholderId = shareholder.Id, TotalAmount = amount, EventDate = new DateOnly(2026, 1, day) });
            await service.ConfirmDocumentsAsync(payment.Id);
            await service.PostEventAsync(payment.Id);
        }
        await Pay(1000m, 3);
        var item = await t.Db.OpenItems.SingleAsync(o => o.JournalEntryId == original.Id);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 4), "PK/KOREKTA",
            new("801", 4000m, 0), new("245-01", 0, 4000m, cp, OpenItemId: item.Id)));
        await t.Db.SaveChangesAsync();
        Assert.Equal(4000m, (await service.UnpaidContributionAsync(shareholder.Id)).Unpaid);
        Assert.Equal(-1000m, await t.BalanceAsync("801"));
        await Pay(4000m, 5);
        Assert.Equal(0m, (await service.UnpaidContributionAsync(shareholder.Id)).Unpaid);
        Assert.Equal(-5000m, await t.BalanceAsync("801"));
        await Assert.ThrowsAsync<DomainException>(() => Pay(1m, 6));
    }

    [Fact]
    public void Reduced_rate_limit_uses_tax_year_rate_full_months_and_rounds_last()
    {
        // 2026: kurs z 2.01.2026 (pierwszy dzień roboczy roku podatkowego) 4,2156 → 8 431 200 → 8 431 000 zł
        Assert.Equal(8_431_000m, CitCalculator.ReducedRateLimitPln(4.2156m, 12));
        // rok krótszy i dłuższy: 1/12 × 2 mln euro × pełne miesiące, przeliczenie i dopiero potem zaokrąglenie
        Assert.Equal(4_216_000m, CitCalculator.ReducedRateLimitPln(4.2156m, 6));    // 1 000 000 € × 4,2156 = 4 215 600 → 4 216 000
        Assert.Equal(12_647_000m, CitCalculator.ReducedRateLimitPln(4.2156m, 18));  // 3 000 000 € × 4,2156 = 12 646 800 → 12 647 000
        Assert.Equal(12, CitCalculator.FullMonths(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(21, CitCalculator.FullMonths(new DateOnly(2026, 3, 15), new DateOnly(2027, 12, 31))); // 657 dni: 21 pełnych okresów 30 dni
        Assert.Equal(8_517_000m, CitCalculator.SmallTaxpayerLimitPln(4.2586m)); // status: kurs z 1.10 roku poprzedniego

        Assert.Equal(9m, CitCalculator.DecideRate(true, false, 4.2156m, 12, 8_431_000m).RatePercent);
        Assert.Equal(19m, CitCalculator.DecideRate(true, false, 4.2156m, 12, 8_431_000.01m).RatePercent); // przekroczenie w bieżącym roku
        Assert.Equal(19m, CitCalculator.DecideRate(true, false, 4.2156m, 6, 5_000_000m).RatePercent);
        Assert.Equal(9m, CitCalculator.DecideRate(true, false, 4.2156m, 18, 10_000_000m).RatePercent);    // rok dłuższy — limit zwiększony
        Assert.Equal(19m, CitCalculator.DecideRate(false, false, 4.2156m, 12, 100m).RatePercent);
        Assert.Equal(19m, CitCalculator.DecideRate(true, true, 4.2156m, 12, 100m).RatePercent);
        Assert.True(CitCalculator.DecideRate(true, false, null, 12, 100m).RateMissing); // brak kursu — bez zastępczego kursu
    }

    [Fact]
    public async Task Capital_gains_are_a_separate_source_taxed_at_19_and_limit_switches_operating_rate()
    {
        await using var t = await TestBooks.CreateAsync();
        var acc = await t.Db.Accounts.FirstAsync(a => a.Number == "750-03");
        acc.CitClassification = CitClassification.PrzychodZyskiKapitalowe;
        await t.Db.SaveChangesAsync();
        var ledger = t.Ledger();
        var d = new DateOnly(2026, 1, 20);
        await ledger.CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 10000m, 0), new("700-01", 0, 10000m)));
        await ledger.CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("402", 2000m, 0), new("130-01", 0, 2000m)));
        await ledger.CreateAndPostAsync(TestBooks.Entry(d, "PK/3", new("130-01", 5000m, 0), new("750-03", 0, 5000m)));
        await t.Db.SaveChangesAsync();

        var cit = new CitService(t.Db, t.User, t.Clock);
        var rec = await cit.ReconcileAsync(t.Year.Id, 1);
        Assert.Equal(9m, rec.Operating.RatePercent); // pierwszy rok, przychody 15 000 zł ≤ limit
        Assert.Equal(8000m, rec.Operating.Income);
        Assert.Equal(720m, rec.Operating.Tax);
        Assert.Equal(5000m, rec.CapitalGains.Income);
        Assert.Equal(950m, rec.CapitalGains.Tax); // zyski kapitałowe zawsze 19%
        Assert.Equal(1670m, rec.Computation.Tax);
        var noRate = await Assert.ThrowsAsync<DomainException>(() => cit.ComputeAdvanceAsync(t.Year.Id, 1)); // brak kursu NBP — zaliczki nie zapisuje się
        Assert.Contains("kurs euro", noRate.Message);
        await cit.SetLimitEurRateAsync(t.Year.StartDate, 4.2156m);
        Assert.Equal(1670m, (await cit.ComputeAdvanceAsync(t.Year.Id, 1)).AdvanceDue);

        // przychody roku przekraczają limit (sztucznie niski kurs) → 19% od dochodu narastająco
        await cit.SetLimitEurRateAsync(t.Year.StartDate, 0.001m); // limit 2 000 zł
        var over = await cit.ReconcileAsync(t.Year.Id, 1);
        Assert.Equal(19m, over.Operating.RatePercent);
        Assert.Equal(1520m + 950m, over.Computation.Tax);
        Assert.Contains("przekroczyły limit", over.Rate.Reason);

        await cit.SetLimitEurRateAsync(t.Year.StartDate, 4.2156m);
        await cit.SetReducedRateExcludedAsync(true);
        Assert.Equal(19m, (await cit.ReconcileAsync(t.Year.Id, 1)).Operating.RatePercent);
    }

    private static async Task<Document> PurchaseAsync(TestBooks t, Guid cpId, string number, decimal net, string? account = null, decimal nkupPercent = 0m)
    {
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 15);
        var doc = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cpId, d, d,
            new[] { new DocumentLineInput("Usługa", 1, net, "23", AccountNumber: account, NkupPercent: nkupPercent) }, DueDate: d.AddDays(14), ExternalNumber: number, ReceiptDate: d));
        await docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100m, false) }, new DateOnly(2026, 1, 1));
        await docs.ApproveAsync(doc.Id);
        await docs.PostAsync(doc.Id);
        return doc;
    }

    [Fact]
    public async Task Unpaid_purchase_increases_cit_only_by_its_kup_part_by_source_and_keeps_history()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var kup = await PurchaseAsync(t, cp.Id, "FZ/1", 1000m);
        await PurchaseAsync(t, cp.Id, "FZ/NKUP", 500m, nkupPercent: 100m); // w całości NKUP — bez zwiększenia
        var capAcc = await t.Db.Accounts.FirstAsync(a => a.Number == "751-03");
        capAcc.CitClassification = CitClassification.KosztZyskiKapitalowe;
        await t.Db.SaveChangesAsync();
        await PurchaseAsync(t, cp.Id, "FZ/KAP", 300m, account: "751-03");
        var person = t.Db.Counterparties.Add(new Domain.Counterparties.Counterparty { CompanyId = t.Company.Id, Code = "OS1", Name = "Jan Kowalski (osoba prywatna)" }).Entity;
        await t.Db.SaveChangesAsync();
        await PurchaseAsync(t, person.Id, "R/1", 200m); // nie transakcja handlowa

        var cit = new CitService(t.Db, t.User, t.Clock);
        foreach (var liability in await t.Db.OpenItems.Where(o => o.Side == Domain.Settlements.OpenItemSide.Zobowiazanie && o.CounterpartyId == cp.Id).ToListAsync())
            await cit.QualifyLiabilityAsync(liability.Id, true, "Zweryfikowano warunki transakcji i wyłączenia art. 18f");
        var march = await cit.ReconcileAsync(t.Year.Id, 3); // termin 29.01 + 90 dni = 29.04
        Assert.DoesNotContain(march.Adjustments, a => a.Basis.Contains("18f"));
        var april = await cit.ReconcileAsync(t.Year.Id, 4);
        var op = Assert.Single(april.Adjustments, a => a.Basis.Contains("18f") && !a.Item.Contains("kapitałowe"));
        Assert.Equal(1000m, op.Amount); // tylko część KUP faktury FZ/1 (netto); NKUP i osoba prywatna pominięte
        Assert.Contains("ust. 1 pkt 2", op.Basis);
        Assert.Equal(300m, Assert.Single(april.Adjustments, a => a.Item.Contains("kapitałowe")).Amount);
        Assert.Equal(march.CapitalGains.TaxCosts - 300m, april.CapitalGains.TaxCosts);
        Assert.Equal(march.Operating.TaxCosts - 1000m, april.Operating.TaxCosts);

        // zapłata po 90. dniu w tym samym roku — zmniejszenie do wysokości zwiększenia, historia zapisana z zaliczką
        var item = await t.Db.OpenItems.SingleAsync(o => o.DocumentId == kup.Id);
        var pay = await t.Ledger().CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 5, 10), "WB/5", new("210", 1230m, 0, cp.Id, OpenItemId: item.Id), new("130-01", 0, 1230m)));
        await t.Ledger().PostAsync(pay.Id);
        await cit.SetLimitEurRateAsync(t.Year.StartDate, 4.2156m);
        var may = await cit.ReconcileAsync(t.Year.Id, 5);
        Assert.Equal(-1000m, Assert.Single(may.Adjustments, a => a.Item.StartsWith("Zapłata") && !a.Item.Contains("kapitałowe")).Amount);
        await cit.ComputeAdvanceAsync(t.Year.Id, 5);
        var history = await t.Db.CitLiabilityAdjustments.Where(h => h.OpenItemId == item.Id).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(1000m, history.Single(h => h.Kind == CitLiabilityAdjustmentKind.Zwiekszenie).AmountOperating);
        Assert.Equal(1000m, history.Single(h => h.Kind == CitLiabilityAdjustmentKind.Zmniejszenie).AmountOperating);
        await cit.ComputeAdvanceAsync(t.Year.Id, 3);
        var afterEarlier = await t.Db.CitLiabilityAdjustments.Where(h => h.OpenItemId == item.Id).ToListAsync();
        Assert.Equal(2, afterEarlier.Count);
        Assert.Equal(1000m, afterEarlier.Single(h => h.Kind == CitLiabilityAdjustmentKind.Zmniejszenie).AmountOperating);
        var later = await t.Db.CitAdvances.SingleAsync(a => a.PeriodNumber == 5);
        await cit.ApproveAdvanceAsync(later.Id, true);
        await Assert.ThrowsAsync<DomainException>(() => cit.QualifyLiabilityAsync(item.Id, false, "Zmiana po zatwierdzeniu"));
        await Assert.ThrowsAsync<DomainException>(() => cit.ComputeAdvanceAsync(t.Year.Id, 3));
        Assert.Equal(2, await t.Db.CitLiabilityAdjustments.CountAsync(h => h.OpenItemId == item.Id));
    }

    [Fact]
    public void Small_taxpayer_rate_uses_october_of_the_previous_tax_year()
    {
        // rok kalendarzowy 2026 → poprzedni rok 2025 → 1.10.2025
        Assert.Equal(new DateOnly(2025, 10, 1), CitCalculator.StatusRateOctober(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)));
        // rok 1.11.2026–31.10.2027 → poprzedni 1.11.2025–31.10.2026 → październik 2026
        Assert.Equal(new DateOnly(2026, 10, 1), CitCalculator.StatusRateOctober(new DateOnly(2025, 11, 1), new DateOnly(2026, 10, 31)));
        // rok 1.04.2026–31.03.2027 → poprzedni 1.04.2025–31.03.2026 → październik 2025
        Assert.Equal(new DateOnly(2025, 10, 1), CitCalculator.StatusRateOctober(new DateOnly(2025, 4, 1), new DateOnly(2026, 3, 31)));
        // poprzedni rok krótki bez października (1.01–30.06.2026) → ostatni październik przed jego końcem
        Assert.Equal(new DateOnly(2025, 10, 1), CitCalculator.StatusRateOctober(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public async Task Rpd_parts_come_from_the_cit_reconciliation_and_explain_the_tax_income()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var ledger = t.Ledger();
        var d = new DateOnly(2026, 1, 20);
        await ledger.CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 20000m, 0), new("700-01", 0, 20000m)));
        await ledger.CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("409-02", 300m, 0), new("130-01", 0, 300m)));  // reprezentacja — NKUP trwały
        await t.Db.SaveChangesAsync();
        var fz = await PurchaseAsync(t, cp.Id, "FZ/1", 1000m);                                                         // niezapłacona → art. 18f
        var valuation = new Application.Settlements.ValuationService(t.Db, t.User, t.Clock);
        var prov = await valuation.CreateProvisionAsync(Domain.Settlements.ProvisionKind.Urlopowa, "Urlopy 2026", 800m, d);
        await valuation.UseProvisionAsync(prov.Id, 200m, new DateOnly(2026, 3, 31), release: true);                    // przejściowe: 800 − 200
        var cit = new CitService(t.Db, t.User, t.Clock);
        await cit.QualifyLiabilityAsync((await t.Db.OpenItems.SingleAsync(o => o.DocumentId == fz.Id)).Id, true, "Transakcja handlowa między przedsiębiorcami, brak wyłączeń");
        await cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.ZwiekszeniePrzychodow, 500m, "Przychód podatkowy ujęty w księgach 2025", "art. 12 ust. 3", bookedInPriorYears: true);
        await cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.DodatkowyKUP, 150m, "KUP 2026 ujęty w księgach 2025", "art. 15 ust. 4", bookedInPriorYears: true);
        await cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.ZwiekszeniePrzychodow, 70m, "Przychód nieujęty w księgach", "art. 12");
        await Assert.ThrowsAsync<DomainException>(() => cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.KosztNKUP, 40m, "Kara umowna", "art. 16 ust. 1 pkt 22")); // bez rodzaju różnicy
        await cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.KosztNKUP, 40m, "Kara umowna", "art. 16 ust. 1 pkt 22", permanentDifference: true);
        await cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.KosztNKUP, 60m, "Niewypłacone wynagrodzenie zlecenia", "art. 16 ust. 1 pkt 57", permanentDifference: false);
        await cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.ZmniejszeniePrzychodow, 20m, "Dywidenda zwolniona", "art. 22 ust. 4", permanentDifference: true);
        await cit.AddAdjustmentAsync(t.Year.Id, 1, CitAdjustmentType.ZmniejszeniePrzychodow, 30m, "Odsetki naliczone niezapłacone", "art. 12 ust. 4 pkt 2", permanentDifference: false);

        var rec = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Equal(300m + 40m, rec.Rpd.K4);     // reprezentacja + kara (trwałe)
        Assert.Equal(1000m + 600m + 60m, rec.Rpd.K5); // art. 18f + rezerwa netto + korekta przejściowa
        Assert.Equal(20m, rec.Rpd.K1);             // przychód zwolniony — trwały
        Assert.Equal(30m, rec.Rpd.K2);             // przychód opodatkowany w przyszłości — przejściowy
        Assert.Equal(500m, rec.Rpd.K3);
        Assert.Equal(150m, rec.Rpd.K6);
        Assert.Equal(70m, rec.Rpd.K7);
        Assert.Equal(rec.TaxRevenue - rec.TaxCosts, rec.Rpd.TaxIncomeFrom(rec.BookResultBeforeTax));
        Assert.DoesNotContain(rec.Warnings, w => w.StartsWith("RPD"));
    }

    [Fact]
    public async Task Jpk_shows_art100_services_in_k11_and_k12_and_never_marks_mpp()
    {
        await using var t = await TestBooks.CreateAsync();
        t.Company.Email = "biuro@przyklad.pl";
        var eu = t.Db.Counterparties.Add(new Domain.Counterparties.Counterparty { CompanyId = t.Company.Id, Code = "UE1", Name = "Client GmbH", CountryCode = "DE", VatEuNumber = "DE123456789" }).Entity;
        var pl = await t.AddCounterpartyAsync("K001", "Klient sp. z o.o.", "7740001454");
        await t.Db.SaveChangesAsync();
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 2, 10);
        // usługa B2B dla podatnika z UE (art. 28b) — np. z art. 100 ust. 1 pkt 4
        var b2b = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, eu.Id, d, d,
            new[] { new DocumentLineInput("Usługa programistyczna", 1, 1000m, "npII") }, DueDate: d.AddDays(14), VatTransactionType: VatTransactionType.UslugiPozaKrajem));
        // faktura krajowa z adnotacją „mechanizm podzielonej płatności”
        var mpp = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, pl.Id, d, d,
            new[] { new DocumentLineInput("Usługa", 1, 20000m, "23") }, DueDate: d.AddDays(14), SplitPayment: true, JpkProcedures: "MPP"));
        foreach (var x in new[] { b2b, mpp }) { await docs.ApproveAsync(x.Id); await docs.PostAsync(x.Id); }

        var vat = new VatService(t.Db, t.User, t.Clock);
        var feb = new DateOnly(2026, 2, 1);
        var decl = await vat.ComputeDeclarationAsync(feb);
        var r = JpkV7Builder.Build(t.Company, feb, await vat.RegisterAsync(VatRegisterType.Sprzedaz, feb), await vat.RegisterAsync(VatRegisterType.Zakup, feb), decl, false,
            new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), new XsdValidator(Path.Combine(AppContext.BaseDirectory, "schemas")));
        var ns = JpkV7Rules.Ns;
        Assert.Equal("1000.00", r.Document.Descendants(ns + "SprzedazWiersz").Elements(ns + "K_11").Single().Value);
        Assert.Equal("1000.00", r.Document.Descendants(ns + "SprzedazWiersz").Elements(ns + "K_12").Single().Value);
        Assert.Equal("1000", r.Document.Descendants(ns + "P_11").Single().Value);
        Assert.Equal("1000", r.Document.Descendants(ns + "P_12").Single().Value);
        Assert.Empty(r.Document.Descendants(ns + "MPP"));
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors));
    }

    [Fact]
    public void Jpk_cit_schedule_depends_on_vat_filing_type()
    {
        var monthly = new Company { VatStatus = VatStatus.CzynnyPodatnikVat, VatPeriod = VatSettlementPeriod.Miesieczny };
        var quarterly = new Company { VatStatus = VatStatus.CzynnyPodatnikVat, VatPeriod = VatSettlementPeriod.Kwartalny };
        var nonVat = new Company { VatStatus = VatStatus.ZwolnionyPodmiotowo };
        Assert.Null(BookkeepingAssistant.JpkCitRequired(monthly, new DateOnly(2025, 1, 1)));
        Assert.Contains("JPK_V7M", BookkeepingAssistant.JpkCitRequired(monthly, new DateOnly(2026, 1, 1)));
        Assert.Null(BookkeepingAssistant.JpkCitRequired(quarterly, new DateOnly(2026, 1, 1)));
        Assert.Contains("JPK_V7K", BookkeepingAssistant.JpkCitRequired(quarterly, new DateOnly(2027, 1, 1)));
        Assert.Null(BookkeepingAssistant.JpkCitRequired(nonVat, new DateOnly(2026, 1, 1)));
        Assert.Contains("pozostali", BookkeepingAssistant.JpkCitRequired(nonVat, new DateOnly(2027, 1, 1)));
        Assert.Contains("50 mln euro", BookkeepingAssistant.JpkCitRequired(quarterly, new DateOnly(2025, 1, 1), pgkOrRevenueOver50mEur: true));
    }

    [Fact]
    public async Task Psa_subscriptions_posted_with_old_logic_are_detected_settled_first_and_corrected_by_storno()
    {
        await using var t = await TestBooks.CreateAsync();
        var cap = new CapitalService(t.Db, t.User, t.Clock);
        var sh = await cap.AddShareholderAsync("Anna Akcjonariuszka", null, false);
        var cp = sh.CounterpartyId!.Value;

        async Task<CapitalEvent> LegacySubscriptionAsync(decimal amount, DateOnly date)
        {
            // wcześniejsza logika: objęcie zaksięgowane Wn należne wkłady / Ma kapitał akcyjny
            var ev = await cap.CreateEventAsync(new CapitalEvent
            {
                Type = CapitalEventType.ObjecieAkcjiWkladPieniezny, EventDate = date, ShareholderId = sh.Id, ShareCapitalAmount = amount,
                ContributionType = ContributionType.Pieniezny, AgreementReference = "Umowa 1/2026", Description = "Objęcie akcji"
            });
            var entry = await t.Ledger().CreateAndPostAsync(TestBooks.Entry(date, "KAP/OLD", new("245-01", amount, 0, cp), new("801", 0, amount)));
            ev.JournalEntryId = entry.Id;
            ev.Status = CapitalEventStatus.Zaksiegowany;
            await t.Db.SaveChangesAsync();
            return ev;
        }

        var paidLegacy = await LegacySubscriptionAsync(1000m, new DateOnly(2026, 1, 2));
        var unpaidLegacy = await LegacySubscriptionAsync(2000m, new DateOnly(2026, 1, 3));
        Assert.Equal(2, (await cap.LegacyPsaSubscriptionsAsync()).Count);
        Assert.Equal(-3000m, await t.BalanceAsync("801"));

        // wpłata najpierw rozlicza stary należny wkład — kapitał nie jest uznawany drugi raz
        var pay = await cap.CreateEventAsync(new CapitalEvent { Type = CapitalEventType.WplataWkladu, EventDate = new DateOnly(2026, 1, 10), ShareholderId = sh.Id, TotalAmount = 1000m });
        await cap.ConfirmDocumentsAsync(pay.Id);
        await cap.PostEventAsync(pay.Id);
        Assert.Equal(-3000m, await t.BalanceAsync("801"));
        Assert.Equal(2000m, await t.BalanceAsync("245-01"));

        // korekta: storno tylko objęcia całkowicie niewpłaconego
        var paidState = (await cap.LegacyPsaSubscriptionsAsync()).Single(l => l.EventId == paidLegacy.Id);
        Assert.Equal(1000m, paidState.Paid);
        await Assert.ThrowsAsync<DomainException>(() => cap.CorrectLegacyPsaSubscriptionAsync(paidLegacy.Id, ""));
        var reversal = await cap.CorrectLegacyPsaSubscriptionAsync(unpaidLegacy.Id, "");
        Assert.Equal(EntryKind.Storno, reversal.Kind);
        Assert.Equal(-1000m, await t.BalanceAsync("801"));
        Assert.Equal(0m, await t.BalanceAsync("245-01"));
        Assert.Single(await cap.LegacyPsaSubscriptionsAsync());
        Assert.Equal(2000m, (await cap.UnpaidContributionAsync(sh.Id)).Unpaid); // objęcie po korekcie — w ewidencji wkładów do wniesienia

        var pay2 = await cap.CreateEventAsync(new CapitalEvent { Type = CapitalEventType.WplataWkladu, EventDate = new DateOnly(2026, 1, 20), ShareholderId = sh.Id, TotalAmount = 2000m });
        await cap.ConfirmDocumentsAsync(pay2.Id);
        await cap.PostEventAsync(pay2.Id);
        Assert.Equal(-3000m, await t.BalanceAsync("801")); // kapitał akcyjny = wniesione wkłady
        Assert.Equal(0m, (await cap.UnpaidContributionAsync(sh.Id)).Unpaid);
        var issues = await new BookkeepingAssistant(t.Db, t.User, t.Clock).CheckAsync(t.Year.Id, null);
        Assert.Single(issues, i => i.Code == "KAPITAL_PSA_OBJECIE");
    }
}

/// <summary>Pełny miesiąc VAT: zgodność ewidencji, deklaracji, ksiąg i JPK_V7M dla typowych transakcji krajowych i zagranicznych.</summary>
public class VatFullMonthScenarioTests
{
    [Fact]
    public async Task Registers_declaration_ledger_and_jpk_agree_for_mixed_month()
    {
        await using var t = await TestBooks.CreateAsync();
        t.Company.Email = "biuro@przyklad.pl";
        var pl = await t.AddCounterpartyAsync("K001", "Klient sp. z o.o.", "7740001454");
        var supplier = await t.AddCounterpartyAsync("D001", "Dostawca S.A.", "5260250274");
        Domain.Counterparties.Counterparty Eu(string code, string name, string country, string vat) =>
            t.Db.Counterparties.Add(new Domain.Counterparties.Counterparty { CompanyId = t.Company.Id, Code = code, Name = name, CountryCode = country, VatEuNumber = vat }).Entity;
        var euClient = Eu("UE1", "Kunde GmbH", "DE", "DE123456789");
        var euSupplier = Eu("UE2", "Fournisseur SARL", "FR", "FR12345678901");
        var cloud = Eu("UE3", "Cloud Ltd", "IE", "IE1234567X");
        await t.Db.SaveChangesAsync();
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 2, 10);

        async Task<Document> SaleAsync(Guid cp, decimal net, string rate, VatTransactionType type)
        {
            var inv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy, cp, d, d,
                new[] { new DocumentLineInput("Pozycja", 1, net, rate) }, DueDate: d.AddDays(14), VatTransactionType: type));
            await docs.ApproveAsync(inv.Id);
            await docs.PostAsync(inv.Id);
            return inv;
        }
        async Task PurchaseAsync(Guid cp, decimal net, string number, decimal deduction, VatTransactionType type)
        {
            var doc = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cp, d, d,
                new[] { new DocumentLineInput("Zakup", 1, net, "23") }, DueDate: d.AddDays(14), ExternalNumber: number, ReceiptDate: d, VatTransactionType: type));
            await docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (deduction, false) }, new DateOnly(2026, 2, 1));
            await docs.ApproveAsync(doc.Id);
            await docs.PostAsync(doc.Id);
        }

        var domestic = await SaleAsync(pl.Id, 10000m, "23", VatTransactionType.Krajowa);
        await SaleAsync(euClient.Id, 5000m, "0WDT", VatTransactionType.WewnatrzwspolnotowaDostawaTowarow);
        await SaleAsync(euClient.Id, 3000m, "npII", VatTransactionType.UslugiPozaKrajem);
        // korekta sprzedaży krajowej in minus (rabat) w tym samym okresie
        var corr = await docs.CreateCorrectionAsync(domestic.Id, "Rabat posprzedażowy", d, new[] { new DocumentLineInput("Pozycja", 1, -1000m, "23") });
        await docs.ApproveAsync(corr.Id);
        await docs.PostAsync(corr.Id);
        await PurchaseAsync(supplier.Id, 2000m, "FZ/1", 50m, VatTransactionType.Krajowa);              // odliczenie 50% (np. samochód)
        await PurchaseAsync(cloud.Id, 1000m, "INV-1", 100m, VatTransactionType.ImportUslug28b);        // import usług
        await PurchaseAsync(euSupplier.Id, 2000m, "WNT-1", 100m, VatTransactionType.WewnatrzwspolnotoweNabycieTowarow);

        var vat = new VatService(t.Db, t.User, t.Clock);
        var feb = new DateOnly(2026, 2, 1);
        var decl = await vat.ComputeDeclarationAsync(feb, 0m);
        Assert.Equal(9000m, decl.Fields["P_19"]);
        Assert.Equal(2070m, decl.Fields["P_20"]);
        Assert.Equal(5000m, decl.Fields["P_21"]);
        Assert.Equal(3000m, decl.Fields["P_11"]);
        Assert.Equal(3000m, decl.Fields["P_12"]);
        Assert.Equal(1000m, decl.Fields["P_29"]);
        Assert.Equal(230m, decl.Fields["P_30"]);
        Assert.Equal(2000m, decl.Fields["P_23"]);
        Assert.Equal(460m, decl.Fields["P_24"]);
        Assert.Equal(9000m + 5000m + 3000m + 1000m + 2000m, decl.Fields["P_37"]); // K_12 jest pozycją „w tym” K_11
        Assert.Equal(2760m, decl.Fields["P_38"]);
        Assert.Equal(230m + 230m + 460m, decl.Fields["P_43"]);                     // 50% z 460 + import usług + WNT
        Assert.Equal(1840m, decl.Fields["P_51"]);

        // księgi: VAT należny i naliczony odpowiadają deklaracji
        Assert.Equal(-2760m, await t.BalanceAsync("221-01"));
        Assert.Equal(920m, await t.BalanceAsync("221-02"));

        var r = JpkV7Builder.Build(t.Company, feb, await vat.RegisterAsync(VatRegisterType.Sprzedaz, feb), await vat.RegisterAsync(VatRegisterType.Zakup, feb), decl, false,
            new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), new XsdValidator(Path.Combine(AppContext.BaseDirectory, "schemas")));
        Assert.True(r.Validation.IsValid, string.Join(Environment.NewLine, r.Validation.Errors));
        var ns = JpkV7Rules.Ns;
        Assert.Equal("1840", r.Document.Descendants(ns + "P_51").Single().Value);
        Assert.Equal("2760.00", r.Document.Descendants(ns + "PodatekNalezny").Single().Value);
        Assert.Equal("920.00", r.Document.Descendants(ns + "PodatekNaliczony").Single().Value);
    }
}

public class OperatorAccessTests
{
    [Fact]
    public async Task First_operator_is_founder_others_start_without_permissions_until_admin_grants_roles()
    {
        await using var t = await TestBooks.CreateAsync();
        var svc = new Application.Security.OperatorAccessService(t.Db, t.Clock);
        var founderId = Guid.NewGuid();
        var founderRoles = await svc.ResolveAsync(founderId, "anna@biuro", t.Company.Id);
        Assert.Contains(Domain.Security.Role.Administrator, founderRoles);
        var founder = new Application.Abstractions.CurrentUser(founderId, "anna@biuro", t.Company.Id, founderRoles);

        var clerkId = Guid.NewGuid();
        Assert.Empty(await svc.ResolveAsync(clerkId, "jan@laptop", t.Company.Id)); // nowy operator — bez uprawnień
        var clerk = new Application.Abstractions.CurrentUser(clerkId, "jan@laptop", t.Company.Id, Array.Empty<Domain.Security.Role>());
        await Assert.ThrowsAnyAsync<Exception>(() => svc.ListAsync(clerk));
        await Assert.ThrowsAnyAsync<Exception>(() => new DocumentService(t.Db, clerk, t.Clock).CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaSprzedazy,
            Guid.NewGuid(), new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 10), new[] { new DocumentLineInput("x", 1, 1m, "23") })));

        await svc.SetAsync(founder, clerkId, new[] { Domain.Security.Role.Fakturzysta }, active: true);
        Assert.Equal(new[] { Domain.Security.Role.Fakturzysta }, await svc.ResolveAsync(clerkId, "jan@laptop", t.Company.Id));
        await Assert.ThrowsAsync<DomainException>(() => svc.SetAsync(founder, founderId, Array.Empty<Domain.Security.Role>(), true)); // własne uprawnienia
        await svc.SetAsync(founder, clerkId, Array.Empty<Domain.Security.Role>(), active: false);
        Assert.Empty(await svc.ResolveAsync(clerkId, "jan@laptop", t.Company.Id));
        Assert.True(await t.Db.AuditEvents.CountAsync(a => a.Action.StartsWith("OPERATOR_")) >= 4); // założyciel, nowy operator, dwie zmiany ról
    }
}

public class TaxLossTests
{
    [Fact]
    public async Task Losses_are_deducted_within_five_years_up_to_half_or_once_up_to_five_million_and_per_source()
    {
        await using var t = await TestBooks.CreateAsync();
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 20), "PK/1", new("130-01", 20000m, 0), new("700-01", 0, 20000m)));
        await t.Db.SaveChangesAsync();
        var cit = new CitService(t.Db, t.User, t.Clock);
        var loss = await cit.AddLossAsync("2025", new DateOnly(2025, 12, 31), false, 10000m, "CIT-8 za 2025");
        var old = await cit.AddLossAsync("2019", new DateOnly(2019, 12, 31), false, 4000m, "CIT-8 za 2019");
        var capital = await cit.AddLossAsync("2024", new DateOnly(2024, 12, 31), true, 3000m, null, deductibleUntil: new DateOnly(2029, 12, 31), deductibleUntilBasis: "CIT-8 za 2024 i 2025"); // rok 2025 poza programem — okres potwierdzony

        await Assert.ThrowsAsync<DomainException>(() => cit.DeductLossAsync(loss.Id, t.Year.Id, 1, 6000m, oneTime: false)); // > 50%
        await Assert.ThrowsAsync<DomainException>(() => cit.DeductLossAsync(old.Id, t.Year.Id, 1, 1000m, oneTime: false));  // po 5 latach
        await cit.DeductLossAsync(loss.Id, t.Year.Id, 1, 5000m, oneTime: false);
        await Assert.ThrowsAsync<DomainException>(() => cit.DeductLossAsync(loss.Id, t.Year.Id, 1, 1000m, oneTime: true)); // drugie odliczenie w roku
        await cit.DeductLossAsync(capital.Id, t.Year.Id, 1, 1500m, oneTime: false);

        var rec = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Equal(15000m, rec.Operating.TaxBase);                     // strata z innych źródeł pomniejsza tylko ich dochód
        Assert.Equal(0m, rec.CapitalGains.TaxBase);
        Assert.Contains(rec.Warnings, w => w.Contains("przekraczają dochód ze źródła")); // brak dochodu z zysków kapitałowych
        Assert.Contains(rec.Adjustments, a => a.Basis.Contains("art. 7 ust. 5"));
        var (_, used, until, projected) = (await cit.LossesAsync()).Single(x => x.Loss.Id == loss.Id);
        Assert.True(projected); // lata 2027–2030 jeszcze nie istnieją — termin prognozowany
        Assert.Equal(5000m, used);
        Assert.Equal(new DateOnly(2030, 12, 31), until);
        Assert.Throws<DomainException>(() => TaxLossRules.Validate(loss, new List<TaxLossDeduction>(), 1, new DateOnly(2026, 12, 31), 5_000_001m, oneTime: true));
        // lata podatkowe, nie kalendarzowe: strata za rok kończący się 30.06.2024; lata 1.07.2024–31.12.2025 (18 mies.), 2026, 2027, 2028, 2029
        var years = new List<(DateOnly, DateOnly)> { (new(2024, 7, 1), new(2025, 12, 31)), (new(2026, 1, 1), new(2026, 12, 31)), (new(2027, 1, 1), new(2027, 12, 31)),
            (new(2028, 1, 1), new(2028, 12, 31)), (new(2029, 1, 1), new(2029, 12, 31)), (new(2030, 1, 1), new(2030, 12, 31)) };
        Assert.Equal(5, TaxLossRules.TaxYearNumber(new DateOnly(2024, 6, 30), years, new DateOnly(2029, 1, 1)));   // kalendarzowo minęłoby 4,5 roku
        Assert.Equal(6, TaxLossRules.TaxYearNumber(new DateOnly(2024, 6, 30), years, new DateOnly(2030, 1, 1)));   // poza okresem, choć przed 30.06.2029+5 lat
        // lata między stratą a programem nieznane — bez szacowania: wymagane potwierdzenie końca okresu odliczenia
        Assert.Null(TaxLossRules.TaxYearNumber(new DateOnly(2023, 12, 31), years.Skip(1).ToList(), new DateOnly(2026, 1, 1)));
        var gapLoss = new TaxLoss { YearLabel = "2023", YearEnd = new DateOnly(2023, 12, 31), Amount = 100m };
        Assert.Null(TaxLossRules.WindowEnd(gapLoss, years.Skip(1).ToList()));
        Assert.Throws<DomainException>(() => TaxLossRules.Validate(gapLoss, new List<TaxLossDeduction>(), null, new DateOnly(2026, 12, 31), 10m, false));
        gapLoss.DeductibleUntil = new DateOnly(2028, 12, 31);                                   // potwierdzone przez użytkownika
        TaxLossRules.Validate(gapLoss, new List<TaxLossDeduction>(), null, new DateOnly(2026, 12, 31), 10m, false);
        Assert.Throws<DomainException>(() => TaxLossRules.Validate(gapLoss, new List<TaxLossDeduction>(), null, new DateOnly(2029, 12, 31), 10m, false));
        // łańcuch lat podatkowych z rokiem 18-miesięcznym: koniec okresu = koniec 5. roku podatkowego
        Assert.Equal(new DateOnly(2029, 12, 31), TaxLossRules.WindowEnd(new TaxLoss { YearEnd = new DateOnly(2024, 6, 30), Amount = 1m }, years));
    }

    [Fact]
    public async Task Debt_financing_costs_over_limit_are_excluded_automatically_as_temporary_difference()
    {
        await using var t = await TestBooks.CreateAsync();
        var d = new DateOnly(2026, 3, 31);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 10_000_000m, 0), new("700-01", 0, 10_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("751-01", 5_000_000m, 0), new("130-01", 0, 5_000_000m)));
        await t.Db.SaveChangesAsync();
        var rec = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        // EBITDA podatkowa 10 mln → limit max(3 mln; 30% × 10 mln) = 3 mln → nadwyżka 2 mln wyłączona z KUP
        Assert.Contains(rec.Adjustments, a => a.Basis.Contains("art. 15c") && a.Amount == 2_000_000m);
        // limit kwotowy proporcjonalny dla roku ≠ 12 miesięcy: 250 000 zł za każdy rozpoczęty miesiąc
        Assert.Equal(3_000_000m, CitCalculator.DebtFinancingAmountLimit(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(750_000m, CitCalculator.DebtFinancingAmountLimit(new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(750_000m, CitCalculator.DebtFinancingAmountLimit(new DateOnly(2026, 10, 15), new DateOnly(2026, 12, 31)));   // rozpoczęte miesiące
        Assert.Equal(4_500_000m, CitCalculator.DebtFinancingAmountLimit(new DateOnly(2025, 7, 1), new DateOnly(2026, 12, 31)));   // 18 miesięcy
        Assert.Equal(3_000_000m, rec.TaxCosts);
        Assert.Equal(2_000_000m, rec.Rpd.K5);
        Assert.Equal(rec.TaxRevenue - rec.TaxCosts, rec.Rpd.TaxIncomeFrom(rec.BookResultBeforeTax));
    }

    [Fact]
    public async Task Financing_costs_marked_on_accounts_count_toward_article_15c()
    {
        await using var t = await TestBooks.CreateAsync();
        var d = new DateOnly(2026, 3, 31);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 10_000_000m, 0), new("700-01", 0, 10_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("751-01", 2_000_000m, 0), new("130-01", 0, 2_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/3", new("751-03", 1_500_000m, 0), new("130-01", 0, 1_500_000m))); // prowizja od kredytu
        await t.Db.SaveChangesAsync();
        var cit = new CitService(t.Db, t.User, t.Clock);
        Assert.DoesNotContain((await cit.ReconcileAsync(t.Year.Id, 12)).Adjustments, a => a.Basis.Contains("art. 15c")); // same odsetki 2 mln < 3 mln
        var fee = await t.Db.Accounts.FirstAsync(a => a.Number == "751-03");
        await new Application.Ledger.ChartService(t.Db, t.User, t.Clock).SetDebtFinancingAsync(fee.Id, DebtFinancingKind.Koszt);
        var rec = await cit.ReconcileAsync(t.Year.Id, 12);
        // koszty finansowania 3,5 mln; EBITDA = 10 mln → limit max(3 mln; 3 mln) → nadwyżka 0,5 mln
        Assert.Contains(rec.Adjustments, a => a.Basis.Contains("art. 15c") && a.Amount == 500_000m);
    }
}

public class AdditionalComplianceTests
{
    [Fact]
    public void Ksef_receipt_date_is_number_assignment_day_in_polish_time()
    {
        var m = new PelnaKsiegowosc.Infrastructure.Ksef.KsefDto.InvoiceMetadata("5260250274-20260131-0123456789AB-CD", "FV/1", new DateOnly(2026, 1, 31),
            new DateTimeOffset(2026, 1, 31, 22, 0, 0, TimeSpan.Zero), null, 100m, 123m, 23m, "PLN", "VAT",
            AcquisitionDate: new DateTimeOffset(2026, 1, 31, 23, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 2, 1), PelnaKsiegowosc.Infrastructure.Ksef.KsefService.ReceiptDate(m)); // 00:30 czasu polskiego 1 lutego
    }

    [Fact]
    public async Task Vat_deduction_window_is_two_following_quarters_for_quarterly_payers()
    {
        await using var t = await TestBooks.CreateAsync();
        t.Company.VatPeriod = VatSettlementPeriod.Kwartalny;
        await t.Db.SaveChangesAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 2, 10);
        var doc = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cp.Id, d, d,
            new[] { new DocumentLineInput("Usługa", 1, 100m, "23") }, DueDate: d.AddDays(14), ExternalNumber: "FZ/K", ReceiptDate: d));
        // otrzymana w I kwartale → odliczenie do końca III kwartału (wrzesień), nie później
        await docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100m, false) }, new DateOnly(2026, 9, 1));
        await Assert.ThrowsAsync<DomainException>(() => docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100m, false) }, new DateOnly(2026, 10, 1)));
        await Assert.ThrowsAsync<DomainException>(() => docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100m, false) }, new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Older_local_backups_can_be_purged_after_anonymization()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pk-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string F(string n, int minutesAgo)
            {
                var p = Path.Combine(dir, n);
                File.WriteAllText(p, n);
                File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-minutesAgo));
                return p;
            }
            F("a.pkbak", 30); F("b.pkbak", 20);
            var fresh = F("c.pkbak", 0);
            F("inny.txt", 60);
            Assert.Equal(2, PelnaKsiegowosc.Infrastructure.Backup.BackupService.PurgeLocalBackupsOlderThan(dir, fresh));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(Path.Combine(dir, "inny.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Debt_financing_excess_is_split_between_income_sources()
    {
        await using var t = await TestBooks.CreateAsync();
        var d = new DateOnly(2026, 3, 31);
        // przychody 10 mln; odsetki: 3 mln w źródle „inne” (751-01) i 1 mln w zyskach kapitałowych (konto 751-02 oznaczone jako koszt finansowania)
        var fx = await t.Db.Accounts.FirstAsync(a => a.Number == "751-02");
        fx.CitClassification = CitClassification.KosztZyskiKapitalowe;
        fx.DebtFinancing = DebtFinancingKind.Koszt;
        await t.Db.SaveChangesAsync();
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 10_000_000m, 0), new("700-01", 0, 10_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("751-01", 3_000_000m, 0), new("130-01", 0, 3_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/3", new("751-02", 1_000_000m, 0), new("130-01", 0, 1_000_000m)));
        await t.Db.SaveChangesAsync();
        var rec = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        // koszty finansowania 4 mln, limit 3 mln → nadwyżka 1 mln: 3/4 ze źródła „inne”, 1/4 z zysków kapitałowych
        Assert.Contains(rec.Adjustments, a => a.Basis.Contains("art. 15c") && !a.Item.Contains("kapitałowe") && a.Amount == 750_000m);
        Assert.Contains(rec.Adjustments, a => a.Basis.Contains("art. 15c") && a.Item.Contains("kapitałowe") && a.Amount == 250_000m);
        Assert.Equal(750_000m, rec.CapitalGains.TaxCosts);
        Assert.Equal(2_250_000m, rec.Operating.TaxCosts);
    }
}

public class DebtFinancingTests
{
    private static async Task<Domain.Accounts.Account> AccountAsync(TestBooks t, string number, CitClassification cit, DebtFinancingKind? kind)
    {
        var a = await t.Db.Accounts.FirstAsync(x => x.Number == number);
        a.CitClassification = cit;
        a.DebtFinancing = kind;
        await t.Db.SaveChangesAsync();
        return a;
    }

    [Fact]
    public async Task Qualification_of_financing_costs_covers_leasing_fees_fx_and_excludes_capitalized_and_nkup()
    {
        await using var t = await TestBooks.CreateAsync();
        var chart = new Application.Ledger.ChartService(t.Db, t.User, t.Clock);
        var leasing = await chart.AddAccountAsync(t.Chart.Id, "751-06", "Część odsetkowa rat leasingu", AccountType.WynikoweKosztowe, profitLossLine: "R.H.IV", cit: CitClassification.KosztPodatkowy);
        await chart.SetDebtFinancingAsync(leasing.Id, DebtFinancingKind.Koszt);
        await AccountAsync(t, "751-01", CitClassification.KosztPodatkowy, null);                      // odsetki — R.H.I, bez oznaczenia
        await AccountAsync(t, "751-02", CitClassification.KosztPodatkowy, DebtFinancingKind.Koszt);   // różnice kursowe od kredytu
        await AccountAsync(t, "751-03", CitClassification.KosztPodatkowy, DebtFinancingKind.NieDotyczy); // opłaty niezwiązane z finansowaniem
        await AccountAsync(t, "750-01", CitClassification.PrzychodPodatkowy, null);                   // przychody odsetkowe — R.G.II
        var d = new DateOnly(2026, 3, 31);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 10_000_000m, 0), new("700-01", 0, 10_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("751-01", 2_000_000m, 0), new("130-01", 0, 2_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/3", new("751-06", 600_000m, 0), new("130-01", 0, 600_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/4", new("751-02", 400_000m, 0), new("130-01", 0, 400_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/5", new("751-03", 500_000m, 0), new("130-01", 0, 500_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/6", new("751-04", 5_000_000m, 0), new("130-01", 0, 5_000_000m)));  // odsetki budżetowe NKUP
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/7", new("080", 3_000_000m, 0), new("130-01", 0, 3_000_000m)));     // odsetki skapitalizowane w ŚT w budowie
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/8", new("130-01", 200_000m, 0), new("750-01", 0, 200_000m)));
        await t.Db.SaveChangesAsync();
        var cit = new CitService(t.Db, t.User, t.Clock);
        // koszty finansowania 2 + 0,6 + 0,4 = 3,0 mln − przychody odsetkowe 0,2 mln = 2,8 mln ≤ limit 3 mln (NKUP, opłaty „nie dotyczy”
        // i odsetki skapitalizowane w wartości środka trwałego — poza limitem w roku poniesienia)
        var rec = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.DoesNotContain(rec.Adjustments, a => a.Basis.Contains("art. 15c"));
        Assert.Equal(200_000m, rec.DebtFinancingHeadroom);
        // opłaty jednak związane z kredytem (art. 15c ust. 12) → 3,3 mln; EBITDA 10 mln → limit 3 mln → nadwyżka 0,3 mln
        await chart.SetDebtFinancingAsync((await t.Db.Accounts.FirstAsync(a => a.Number == "751-03")).Id, DebtFinancingKind.Koszt);
        rec = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Contains(rec.Adjustments, a => a.Basis.Contains("art. 15c ust. 1") && a.Amount == 300_000m);
        Assert.Equal(300_000m, rec.DebtFinancingExcessOperating);
        Assert.Equal(0m, rec.DebtFinancingHeadroom);
        Assert.Equal(rec.TaxRevenue - rec.TaxCosts, rec.Rpd.TaxIncomeFrom(rec.BookResultBeforeTax));
    }

    [Fact]
    public async Task Excess_from_prior_years_is_deductible_within_five_years_and_this_years_headroom()
    {
        await using var t = await TestBooks.CreateAsync();
        await AccountAsync(t, "751-01", CitClassification.KosztPodatkowy, null);
        var d = new DateOnly(2026, 3, 31);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 10_000_000m, 0), new("700-01", 0, 10_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("751-01", 1_000_000m, 0), new("130-01", 0, 1_000_000m)));
        await t.Db.SaveChangesAsync();
        var cit = new CitService(t.Db, t.User, t.Clock);
        var before = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Equal(2_000_000m, before.DebtFinancingHeadroom);                      // limit 3 mln − 1 mln

        // lata sprzed programu: koniec okresu rozliczenia trzeba wskazać (nie szacuje się długości lat)
        const string Stmt = "oświadczenie: koszty własne spółki, brak sukcesji i PGK (KRS)";
        var noWindow = await cit.AddDebtFinancingCarryforwardAsync("2023", new DateOnly(2023, 12, 31), false, 100_000m, "CIT-8 za 2023");
        await Assert.ThrowsAsync<DomainException>(() => cit.UseDebtFinancingCarryforwardAsync(noWindow.Id, t.Year.Id, 10_000m, Stmt));
        await Assert.ThrowsAsync<DomainException>(() => cit.AddDebtFinancingCarryforwardAsync("2018", new DateOnly(2018, 12, 31), false, 1m, " "));   // bez źródła kwoty
        // ręczny koniec okresu: wymaga dokumentu, ostatniego dnia miesiąca i zgodności z latami w programie (2026 — kończy się 31.12.2026)
        await Assert.ThrowsAsync<DomainException>(() => cit.AddDebtFinancingCarryforwardAsync("2021", new DateOnly(2021, 12, 31), false, 1m, "CIT-8", new DateOnly(2026, 12, 31)));
        await Assert.ThrowsAsync<DomainException>(() => cit.AddDebtFinancingCarryforwardAsync("2021", new DateOnly(2021, 12, 31), false, 1m, "CIT-8", new DateOnly(2026, 6, 30), deductibleUntilBasis: "CIT-8 2022–2025"));
        await Assert.ThrowsAsync<DomainException>(() => cit.AddDebtFinancingCarryforwardAsync("2021", new DateOnly(2021, 12, 31), false, 1m, "CIT-8", new DateOnly(2026, 12, 15), deductibleUntilBasis: "CIT-8 2022–2025"));
        var expired = await cit.AddDebtFinancingCarryforwardAsync("2019", new DateOnly(2019, 12, 31), false, 100_000m, "CIT-8 za 2019", new DateOnly(2024, 12, 31), deductibleUntilBasis: "CIT-8 2020–2024");
        await Assert.ThrowsAsync<DomainException>(() => cit.UseDebtFinancingCarryforwardAsync(expired.Id, t.Year.Id, 10_000m, Stmt)); // po 5 latach
        await Assert.ThrowsAsync<DomainException>(() => cit.AddDebtFinancingCarryforwardAsync("2019", new DateOnly(2019, 12, 31), false, 1m, "CIT-8")); // duplikat

        var c2025 = await cit.AddDebtFinancingCarryforwardAsync("2025", new DateOnly(2025, 12, 31), false, 800_000m, "CIT-8 za 2025", new DateOnly(2030, 12, 31), deductibleUntilBasis: "rok podatkowy = kalendarzowy (umowa spółki)");
        var c2024 = await cit.AddDebtFinancingCarryforwardAsync("2024", new DateOnly(2024, 12, 31), false, 1_500_000m, "CIT-8 za 2024", new DateOnly(2029, 12, 31), deductibleUntilBasis: "CIT-8 za 2025, rok kalendarzowy");
        await Assert.ThrowsAsync<DomainException>(() => cit.UseDebtFinancingCarryforwardAsync(c2025.Id, t.Year.Id, 100m, " "));      // bez oświadczenia (art. 15c ust. 19)
        await Assert.ThrowsAsync<DomainException>(() => cit.UseDebtFinancingCarryforwardAsync(c2025.Id, t.Year.Id, 900_000m, Stmt)); // ponad pozostałą kwotę
        await cit.UseDebtFinancingCarryforwardAsync(c2025.Id, t.Year.Id, 800_000m, Stmt);
        await Assert.ThrowsAsync<DomainException>(() => cit.UseDebtFinancingCarryforwardAsync(c2024.Id, t.Year.Id, 1_300_000m, Stmt)); // wolny limit 1,2 mln
        await cit.UseDebtFinancingCarryforwardAsync(c2024.Id, t.Year.Id, 1_200_000m, Stmt);
        await Assert.ThrowsAsync<DomainException>(() => cit.UseDebtFinancingCarryforwardAsync(c2024.Id, t.Year.Id, 1m, Stmt));        // drugi raz w tym roku
        Assert.Contains(await t.Db.AuditEvents.Where(a => a.Action == "CIT_15C_ZALICZENIE").Select(a => a.Details).ToListAsync(), d => d!.Contains("ust. 18") && d.Contains("ust. 19"));

        var rec = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Equal(before.TaxCosts + 2_000_000m, rec.TaxCosts);
        Assert.Contains(rec.Adjustments, a => a.Item.Contains("z roku 2025") && a.Amount == -800_000m && a.Basis.Contains("art. 15c ust. 18"));
        Assert.Equal(before.Rpd.K6 + 2_000_000m, rec.Rpd.K6);                              // koszty ujęte w księgach lat ubiegłych
        Assert.Equal(rec.TaxRevenue - rec.TaxCosts, rec.Rpd.TaxIncomeFrom(rec.BookResultBeforeTax));
        // zaliczki (okresy przed rocznym rozliczeniem) — bez nadwyżek z lat ubiegłych
        Assert.DoesNotContain((await cit.ReconcileAsync(t.Year.Id, 6)).Adjustments, a => a.Basis.Contains("art. 15c ust. 18"));

        var register = await cit.DebtFinancingCarryforwardsAsync();
        Assert.Equal(300_000m, register.Single(r => r.Item.Id == c2024.Id) is var x ? x.Item.Amount - x.Used : -1);
        // nadwyżkę z bieżącego wyliczenia rejestruje się dopiero po zamknięciu roku
        await Assert.ThrowsAsync<DomainException>(() => cit.RegisterDebtFinancingExcessFromYearAsync(t.Year.Id));
    }
}

public class ConfirmedPeriodEndTests
{
    [Fact]
    public void Manually_confirmed_five_year_end_must_match_known_tax_years_and_have_a_documented_basis()
    {
        var years = new List<(DateOnly, DateOnly)> { (new(2025, 7, 1), new(2026, 12, 31)), (new(2027, 1, 1), new(2027, 12, 31)) }; // rok zmieniony 18 mies.
        var lossEnd = new DateOnly(2025, 6, 30);
        Assert.Throws<DomainException>(() => TaxLossRules.ValidateConfirmedEnd(lossEnd, new DateOnly(2030, 6, 30), null, years));            // brak dokumentu
        Assert.Throws<DomainException>(() => TaxLossRules.ValidateConfirmedEnd(lossEnd, new DateOnly(2027, 6, 30), "CIT-8", years));         // w trakcie roku z programu
        Assert.Throws<DomainException>(() => TaxLossRules.ValidateConfirmedEnd(lossEnd, new DateOnly(2027, 12, 31), "CIT-8", years));        // program zna 2 lata — 5. rok później
        Assert.Throws<DomainException>(() => TaxLossRules.ValidateConfirmedEnd(lossEnd, new DateOnly(2030, 12, 30), "CIT-8", years));        // nie koniec miesiąca
        Assert.Throws<DomainException>(() => TaxLossRules.ValidateConfirmedEnd(lossEnd, new DateOnly(2036, 12, 31), "CIT-8", years));        // ponad 10 lat
        TaxLossRules.ValidateConfirmedEnd(lossEnd, new DateOnly(2030, 12, 31), "uchwała o zmianie roku podatkowego, CIT-8", years);
        // pięć lat w programie — tylko koniec 5. roku
        var five = Enumerable.Range(0, 5).Select(i => (new DateOnly(2021 + i, 1, 1), new DateOnly(2021 + i, 12, 31))).ToList();
        Assert.Throws<DomainException>(() => TaxLossRules.ValidateConfirmedEnd(new DateOnly(2020, 12, 31), new DateOnly(2026, 12, 31), "CIT-8", five));
        TaxLossRules.ValidateConfirmedEnd(new DateOnly(2020, 12, 31), new DateOnly(2025, 12, 31), "CIT-8", five);
    }
}

public class DebtFinancingControlCalculationTests
{
    /// <summary>
    /// Wyliczenie kontrolne (niezależne, ręczne): przychody 10 000 000 zł, odsetki 1 000 000 zł → limit max(3 mln; 30% × 10 mln) = 3 mln,
    /// wolny limit 2 000 000 zł. Koszty wyłączone w latach ubiegłych: 2022 „inne” 500 000, 2023 „zyski kapitałowe” 700 000,
    /// 2024 „inne” 1 000 000. Zaliczenie: 500 000 + 700 000 + 800 000 = 2 000 000 (cały wolny limit).
    /// KUP „inne” = 1 000 000 + 500 000 + 800 000 = 2 300 000; KUP „zyski kapitałowe” = 700 000 (strata w tym źródle 700 000).
    /// Po korekcie (dodatkowe odsetki 1 500 000) wolny limit = 3 mln − 2,5 mln = 500 000 → uwzględnia się tylko 500 000 (najstarsza: 2022).
    /// </summary>
    [Fact]
    public async Task Several_prior_year_amounts_from_two_sources_match_hand_calculation_and_later_correction_caps_them()
    {
        await using var t = await TestBooks.CreateAsync();
        var acc = await t.Db.Accounts.FirstAsync(a => a.Number == "751-01");
        acc.CitClassification = CitClassification.KosztPodatkowy;
        await t.Db.SaveChangesAsync();
        var d = new DateOnly(2026, 3, 31);
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/1", new("130-01", 10_000_000m, 0), new("700-01", 0, 10_000_000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(d, "PK/2", new("751-01", 1_000_000m, 0), new("130-01", 0, 1_000_000m)));
        await t.Db.SaveChangesAsync();
        var cit = new CitService(t.Db, t.User, t.Clock);
        const string Stmt = "koszty własne spółki; brak przekształceń, połączeń, podziałów i PGK (KRS)";
        var c22 = await cit.AddDebtFinancingCarryforwardAsync("2022", new DateOnly(2022, 12, 31), false, 500_000m, "CIT-8 za 2022", new DateOnly(2027, 12, 31), deductibleUntilBasis: "CIT-8 2023–2025");
        var c23 = await cit.AddDebtFinancingCarryforwardAsync("2023", new DateOnly(2023, 12, 31), true, 700_000m, "CIT-8 za 2023", new DateOnly(2028, 12, 31), deductibleUntilBasis: "CIT-8 2024–2025");
        var c24 = await cit.AddDebtFinancingCarryforwardAsync("2024", new DateOnly(2024, 12, 31), false, 1_000_000m, "CIT-8 za 2024", new DateOnly(2029, 12, 31), deductibleUntilBasis: "CIT-8 za 2025");
        Assert.Equal(2_000_000m, (await cit.ReconcileAsync(t.Year.Id, 12)).DebtFinancingHeadroom);
        await cit.UseDebtFinancingCarryforwardAsync(c22.Id, t.Year.Id, 500_000m, Stmt);
        await cit.UseDebtFinancingCarryforwardAsync(c23.Id, t.Year.Id, 700_000m, Stmt);
        await cit.UseDebtFinancingCarryforwardAsync(c24.Id, t.Year.Id, 800_000m, Stmt);
        await Assert.ThrowsAsync<DomainException>(() => cit.UseDebtFinancingCarryforwardAsync(c24.Id, t.Year.Id, 1m, Stmt));

        var rec = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Equal(2_300_000m, rec.Operating.TaxCosts);
        Assert.Equal(700_000m, rec.CapitalGains.TaxCosts);
        Assert.Equal(7_700_000m, rec.Operating.Income);
        Assert.Equal(700_000m, rec.CapitalGains.Loss);
        Assert.Equal(rec.TaxRevenue - rec.TaxCosts, rec.Rpd.TaxIncomeFrom(rec.BookResultBeforeTax));
        Assert.Equal(200_000m, (await cit.DebtFinancingCarryforwardsAsync()).Single(x => x.Item.Id == c24.Id) is var r24 ? r24.Item.Amount - r24.Used : -1);

        // późniejsza korekta kosztów finansowania roku zmniejsza wolny limit — uwzględnia się tylko część mieszczącą się w limicie
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 12, 31), "PK/3", new("751-01", 1_500_000m, 0), new("130-01", 0, 1_500_000m)));
        await t.Db.SaveChangesAsync();
        rec = await cit.ReconcileAsync(t.Year.Id, 12);
        Assert.Equal(500_000m, rec.DebtFinancingHeadroom);
        Assert.Equal(2_500_000m + 500_000m, rec.Operating.TaxCosts);                    // tylko 2022 (500 000) — najstarsza
        Assert.Equal(0m, rec.CapitalGains.TaxCosts);
        Assert.Contains(rec.Warnings, w => w.Contains("przekracza wolny limit") && w.Contains("2023"));
        Assert.Contains(rec.Warnings, w => w.Contains("przekracza wolny limit") && w.Contains("2024"));
        Assert.Equal(rec.TaxRevenue - rec.TaxCosts, rec.Rpd.TaxIncomeFrom(rec.BookResultBeforeTax));
    }
}

public class ForeignCurrencyDocumentTests
{
    [Fact]
    public async Task Foreign_currency_purchase_without_confirmed_rate_cannot_be_approved_or_posted()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D010", "Supplier GmbH");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 3, 10);
        // jak przy imporcie z KSeF: 1000 EUR z kursem tymczasowym 1,00 bez tabeli i daty
        var input = new InvoiceInput(DocumentKind.FakturaZakupu, cp.Id, d, d, new[] { new DocumentLineInput("Usługa", 1, 1000m, "zw") },
            DueDate: d.AddDays(14), Currency: "EUR", ExchangeRate: 1m, ExternalNumber: "INV-1", ReceiptDate: d);
        var doc = await docs.CreateInvoiceAsync(input);
        var v = await docs.ValidateAsync(await docs.GetAsync(doc.Id));
        Assert.Contains(v.Errors, e => e.Contains("brak potwierdzonego kursu"));
        await Assert.ThrowsAsync<DomainException>(() => docs.ApproveAsync(doc.Id));
        await Assert.ThrowsAsync<DomainException>(() => docs.PostAsync(doc.Id));
        // po wpisaniu kursu NBP z dnia poprzedzającego — 1000 EUR × 4,2512 = 4251,20 zł
        await docs.UpdateInvoiceAsync(doc.Id, input with { ExchangeRate = 4.2512m, ExchangeRateTable = "047/A/NBP/2026", ExchangeRateDate = new DateOnly(2026, 3, 9) });
        Assert.DoesNotContain((await docs.ValidateAsync(await docs.GetAsync(doc.Id))).Errors, e => e.Contains("kursu"));
        Assert.Equal(4251.20m, (await docs.GetAsync(doc.Id)).NetAmountPln);
    }
}

public class CompanyIsolationTests
{
    [Fact]
    public async Task Operator_of_one_unit_cannot_read_post_reverse_or_change_entries_of_another_unit()
    {
        await using var t = await TestBooks.CreateAsync();
        var entry = await t.Ledger().CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 10), "PK/OBCY", new("130-01", 100m, 0), new("801", 0, 100m)));
        var posted = await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 1, 11), "PK/OBCY2", new("130-01", 50m, 0), new("801", 0, 50m)));
        await t.Db.SaveChangesAsync();
        // zapisy „innej jednostki” w tej samej bazie
        var other = Guid.NewGuid();
        await t.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"JournalEntries\" SET \"CompanyId\" = {other} WHERE \"Id\" = {entry.Id}");
        t.Db.ChangeTracker.Clear();
        var ledger = t.Ledger();
        await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(entry.Id));
        await Assert.ThrowsAsync<DomainException>(() => ledger.DeleteDraftAsync(entry.Id));

        // kontekst sesji operatora: dane innej jednostki niewidoczne, a próba zapisu cudzego rekordu odrzucana
        t.Db.CompanyScope = t.Company.Id;
        Assert.False(await t.Db.JournalEntries.AnyAsync(e => e.Id == entry.Id));
        Assert.True(await t.Db.JournalEntries.AnyAsync(e => e.Id == posted.Id));
        t.Db.CompanyScope = null;
        var foreign = await t.Db.JournalEntries.FirstAsync(e => e.Id == entry.Id);
        t.Db.CompanyScope = t.Company.Id;
        foreign.Description = "zmiana";
        Assert.Throws<InvalidOperationException>(() => t.Db.SaveChanges());
        t.Db.ChangeTracker.Clear();
        t.Db.CompanyScope = null;
    }
}

public class FiledVatDeclarationTests
{
    [Fact]
    public async Task Carried_forward_surplus_comes_from_latest_filed_declaration_including_corrections()
    {
        await using var t = await TestBooks.CreateAsync();
        var vat = new VatService(t.Db, t.User, t.Clock);
        var jan = new DateOnly(2026, 1, 1);
        var feb = new DateOnly(2026, 2, 1);
        Assert.Contains((await vat.ComputeDeclarationAsync(feb)).Warnings, w => w.Contains("brak zarejestrowanej złożonej deklaracji"));
        await Assert.ThrowsAsync<DomainException>(() => vat.RecordFiledDeclarationAsync(jan, true, new DateOnly(2026, 2, 20), "UPO-K", 0, 100, 0)); // korekta bez pierwotnej
        await vat.RecordFiledDeclarationAsync(jan, false, new DateOnly(2026, 2, 20), "UPO-1", 0, 1200, 0);
        await Assert.ThrowsAsync<DomainException>(() => vat.RecordFiledDeclarationAsync(jan, false, new DateOnly(2026, 2, 21), "UPO-2", 0, 1, 0));
        Assert.Equal(1200m, (await vat.ComputeDeclarationAsync(feb)).CarriedForwardFromPrevious);
        // korekta deklaracji za styczeń zmienia nadwyżkę przenoszoną do lutego
        await vat.RecordFiledDeclarationAsync(jan, true, new DateOnly(2026, 3, 5), "UPO-K1", 0, 900, 0);
        var d = await vat.ComputeDeclarationAsync(feb);
        Assert.Equal(900m, d.CarriedForwardFromPrevious);
        Assert.Contains(d.Warnings, w => w.Contains("UPO-K1") && w.Contains("korekta"));
        Assert.Equal(2, (await vat.FiledDeclarationsAsync(jan)).Count);
    }
}

public class CitPaymentsTests
{
    [Fact]
    public async Task Cit8_matches_cash_payments_to_advance_deadlines_and_reports_arrears_without_calling_it_overpayment()
    {
        await using var t = await TestBooks.CreateAsync();
        foreach (var (no, due, deadline) in new[] { (1, 1000m, new DateOnly(2026, 2, 20)), (2, 500m, new DateOnly(2026, 3, 20)) })
            t.Db.CitAdvances.Add(new CitAdvance { CompanyId = t.Company.Id, FiscalYearId = t.Year.Id, PeriodNumber = no, PeriodEnd = new DateOnly(2026, no, 28),
                AdvanceDue = due, PaymentDeadline = deadline, IsApproved = true, UserId = t.User.UserId, CalculatedAtUtc = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 2, 15), "WB/1", new("220-01", 1000m, 0), new("130-01", 0, 1000m)));
        await t.Ledger().CreateAndPostAsync(TestBooks.Entry(new DateOnly(2026, 3, 25), "WB/2", new("220-01", 300m, 0), new("130-01", 0, 300m)));
        await t.Db.SaveChangesAsync();
        var r = await new Application.Reports.ReportService(t.Db, t.User, t.Clock).Cit8Async(t.Year.Id);
        string Label(object? c) => c as string ?? "";
        Assert.Equal(1300m, r.Rows.Single(x => (string?)x.Cells[0] == "G.5").Cells[2]);
        Assert.Contains(r.Rows, x => Label(x.Cells[1]).Contains("okres 1") && Label(x.Cells[1]).Contains("w terminie"));
        Assert.Contains(r.Rows, x => Label(x.Cells[1]).Contains("okres 2") && Label(x.Cells[1]).Contains("ZALEGŁOŚĆ 200,00"));
        Assert.Contains(r.Notes, n => n.Contains("zaległość podatkowa"));
        Assert.DoesNotContain(r.Rows, x => Label(x.Cells[1]) == "Nadpłata");
        Assert.Contains(r.Rows, x => Label(x.Cells[1]).Contains("SZACUNEK"));

        // zarachowanie potwierdzone w e-US: wpłata z 25.03 (300 zł) zarachowana na zaliczkę za okres 1 — inny wynik niż szacunek
        var cit = new CitService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => cit.RecordPaymentAllocationAsync(t.Year.Id, new DateOnly(2026, 2, 15), 1000m, 1, false, " "));
        var wrong = await cit.RecordPaymentAllocationAsync(t.Year.Id, new DateOnly(2026, 2, 15), 1000m, 1, false, "e-US — karta rozliczeń");
        // podwójne zarachowanie tej samej wpłaty — odrzucone
        await Assert.ThrowsAsync<DomainException>(() => cit.RecordPaymentAllocationAsync(t.Year.Id, new DateOnly(2026, 2, 15), 300m, 2, false, "e-US"));
        // korekta (anulowanie z przyczyną) — historia zachowana
        await cit.CancelPaymentAllocationAsync(t.Year.Id, wrong.Id, "Urząd zarachował część wpłaty na zaliczkę za okres 2");
        Assert.Single(await cit.PaymentAllocationHistoryAsync("2026"), a => a.Cancelled);
        await cit.RecordPaymentAllocationAsync(t.Year.Id, new DateOnly(2026, 2, 15), 700m, 1, false, "e-US — karta rozliczeń");
        await cit.RecordPaymentAllocationAsync(t.Year.Id, new DateOnly(2026, 3, 25), 300m, 1, false, "e-US — karta rozliczeń");
        await cit.RecordPaymentAllocationAsync(t.Year.Id, new DateOnly(2026, 2, 15), 300m, 2, false, "e-US — karta rozliczeń");
        var c = await new Application.Reports.ReportService(t.Db, t.User, t.Clock).Cit8Async(t.Year.Id);
        Assert.Contains(c.Rows, x => Label(x.Cells[1]).Contains("WPROWADZONE PRZEZ OPERATORA"));
        Assert.Contains(c.Rows, x => Label(x.Cells[1]).Contains("okres 1") && Label(x.Cells[1]).Contains("po terminie"));
        Assert.Contains(c.Rows, x => Label(x.Cells[1]).Contains("okres 2") && Label(x.Cells[1]).Contains("ZALEGŁOŚĆ 200,00"));
    }
}
