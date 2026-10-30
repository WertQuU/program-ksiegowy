using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Assistant;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Accounts;
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
    [Fact]
    public void Reduced_rate_requires_status_no_exclusion_and_current_year_revenue_within_limit()
    {
        // 2 mln euro × 4,2617 = 8 523 400 → zaokrąglenie do 1000 zł
        Assert.Equal(8_523_000m, CitCalculator.ReducedRateLimitPln(4.2617m, 12));
        // rok krótszy niż 12 miesięcy — limit proporcjonalny; dłuższy — bez zwiększenia
        Assert.Equal(4_261_500m, CitCalculator.ReducedRateLimitPln(4.2617m, 6));
        Assert.Equal(8_523_000m, CitCalculator.ReducedRateLimitPln(4.2617m, 18));

        Assert.Equal(9m, CitCalculator.DecideRate(true, false, 4.2617m, 12, 8_523_000m).RatePercent);
        Assert.Equal(19m, CitCalculator.DecideRate(true, false, 4.2617m, 12, 8_523_000.01m).RatePercent); // przekroczenie limitu w bieżącym roku
        Assert.Equal(19m, CitCalculator.DecideRate(true, false, 4.2617m, 6, 5_000_000m).RatePercent);    // limit proporcjonalny
        Assert.Equal(19m, CitCalculator.DecideRate(false, false, 4.2617m, 12, 100m).RatePercent);        // brak statusu
        Assert.Equal(19m, CitCalculator.DecideRate(true, true, 4.2617m, 12, 100m).RatePercent);          // wyłączenie art. 19 ust. 1b
        var fallback = CitCalculator.DecideRate(true, false, null, 12, 100m);
        Assert.True(fallback.FallbackRateUsed);
        Assert.Equal(8_000_000m, fallback.LimitPln); // ostrożny kurs zastępczy zaniża limit
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
        Assert.Equal(1670m, (await cit.ComputeAdvanceAsync(t.Year.Id, 1)).AdvanceDue);

        // przychody roku przekraczają limit (sztucznie niski kurs) → 19% od dochodu narastająco
        await cit.SetEurRateAsync(2026, 0.001m); // limit 2 000 zł
        var over = await cit.ReconcileAsync(t.Year.Id, 1);
        Assert.Equal(19m, over.Operating.RatePercent);
        Assert.Equal(1520m + 950m, over.Computation.Tax);
        Assert.Contains("przekroczyły limit", over.Rate.Reason);

        await cit.SetEurRateAsync(2026, 4.2617m);
        await cit.SetReducedRateExcludedAsync(true);
        Assert.Equal(19m, (await cit.ReconcileAsync(t.Year.Id, 1)).Operating.RatePercent);
    }

    [Fact]
    public async Task Unpaid_purchase_increases_cit_income_in_period_when_90_days_elapse()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("D001", "Dostawca", "7740001454");
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 15);
        var doc = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, cp.Id, d, d,
            new[] { new DocumentLineInput("Usługa", 1, 1000m, "23") }, DueDate: d.AddDays(14), ExternalNumber: "FZ/1", ReceiptDate: d));
        await docs.SetVatDeductionAsync(doc.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100m, false) }, new DateOnly(2026, 1, 1));
        await docs.ApproveAsync(doc.Id);
        await docs.PostAsync(doc.Id);

        var cit = new CitService(t.Db, t.User, t.Clock);
        var march = await cit.ReconcileAsync(t.Year.Id, 3); // termin 29.01 + 90 dni = 29.04
        Assert.DoesNotContain(march.Adjustments, a => a.Basis.Contains("18f"));
        var april = await cit.ReconcileAsync(t.Year.Id, 4);
        var row = Assert.Single(april.Adjustments, a => a.Basis.Contains("18f"));
        Assert.Equal(1000m, row.Amount); // kwota netto (VAT odliczony nie był kosztem)
        Assert.Equal(march.TaxCosts - 1000m, april.TaxCosts);
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
    }
}
