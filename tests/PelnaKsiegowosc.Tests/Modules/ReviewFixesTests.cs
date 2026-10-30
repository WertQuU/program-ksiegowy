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
            // stan sprzed wersji 1.0.1: objęcie zaksięgowane Wn należne wkłady / Ma kapitał akcyjny
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
