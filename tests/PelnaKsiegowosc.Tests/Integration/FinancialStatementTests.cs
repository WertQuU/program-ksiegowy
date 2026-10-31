using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Export;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Tests.Integration;

/// <summary>RZiS kalkulacyjny, zestawienie zmian w kapitale, rachunek przepływów, informacja dodatkowa, e-Sprawozdanie, schematy XSD.</summary>
public class FinancialStatementTests
{
    [Fact]
    public async Task Calculation_variant_equity_changes_and_cash_flow_are_consistent_with_balance_sheet()
    {
        await using var t = await FullYearScenarioTests.BuildFullYearAsync(closeYear: false);
        var sb = new StatementBuilder(t.Db);
        var y = t.Year;

        var calc = await sb.ProfitAndLossCalculationAsync(y.Id, y.EndDate, CostAllocationKey.Default);
        Assert.Empty(calc.Warnings);
        Assert.Equal(10000m, calc.Get("K.A"));
        Assert.Equal(3180m, calc.Get("K.B.I")); // 60% × (3 300 amortyzacja + 2 000 usługi)
        Assert.Equal(530m, calc.Get("K.D"));
        Assert.Equal(1590m, calc.Get("K.E"));
        Assert.Equal(4700m, calc.Get("K.F"));
        Assert.Equal(423m, calc.Get("K.M"));
        Assert.Equal(4277m, calc.Get("K.O"));
        var other = await sb.ProfitAndLossCalculationAsync(y.Id, y.EndDate, new CostAllocationKey(0m, 0m, 100m));
        Assert.Equal(4277m, other.Get("K.O"));
        Assert.Equal(5300m, other.Get("K.E"));
        Assert.Throws<DomainException>(() => new CostAllocationKey(50m, 10m, 10m).Validate());

        var eq = await sb.EquityChangesAsync(y.Id, y.EndDate);
        Assert.Empty(eq.Warnings);
        Assert.Equal(0m, eq.Opening);
        Assert.Equal(14277m, eq.Closing);
        Assert.Equal(1000m, eq.Rows.Single(r => r.Code == "P.A.I").Increase);
        Assert.Equal(9000m, eq.Rows.Single(r => r.Code == "P.A.II").Increase);
        Assert.Equal(4277m, eq.Rows.Single(r => r.Code == "P.A.VI").Increase);

        var cf = await sb.CashFlowAsync(y.Id, y.EndDate);
        Assert.Empty(cf.Warnings);
        Assert.Equal(4277m, cf.Get("A.I"));
        Assert.Equal(3300m, cf.Get("A.II.1"));
        Assert.Equal(9540m, cf.Get("A.III"));
        // wydatki brutto: zapłata faktury za środek trwały 14 760 zł — 12 000 zł inwestycyjnie (wartość netto), VAT — operacyjnie
        Assert.Equal(12000m, cf.Get("B.II.1"));
        Assert.Equal(-12000m, cf.Get("B.III"));
        Assert.Equal(10000m, cf.Get("C.I.1"));                                           // wpłata na kapitał
        Assert.Equal(10000m, cf.Get("C.III"));
        Assert.Equal(7540m, cf.Get("D"));
        Assert.Equal(7540m, cf.Get("E"));
        Assert.Equal(7540m, cf.Get("G"));
    }

    [Fact]
    public async Task Next_year_statements_start_from_opening_balance()
    {
        await using var t = await FullYearScenarioTests.BuildFullYearAsync(closeYear: true);
        var next = await t.Db.FiscalYears.AsNoTracking().SingleAsync(f => f.Name == "2027");
        var sb = new StatementBuilder(t.Db);
        var eq = await sb.EquityChangesAsync(next.Id, next.EndDate);
        Assert.Empty(eq.Warnings);
        Assert.Equal(14277m, eq.Opening);
        Assert.Equal(4277m, eq.Rows.Single(r => r.Code == "P.A.V").Opening);
        var cf = await sb.CashFlowAsync(next.Id, next.EndDate);
        Assert.Empty(cf.Warnings);
        Assert.Equal(7540m, cf.Get("F"));
        Assert.Equal(0m, cf.Get("D"));
    }

    [Fact]
    public async Task Reports_notes_and_electronic_statement_include_new_parts()
    {
        await using var t = await FullYearScenarioTests.BuildFullYearAsync(closeYear: true);
        var rs = new ReportService(t.Db, t.User, t.Clock);
        var y = t.Year;
        await rs.SaveCostAllocationKeyAsync(new CostAllocationKey(70m, 10m, 20m));
        Assert.Equal(70m, (await rs.CostAllocationKeyAsync()).ProductionPercent);

        var calc = await rs.ProfitAndLossCalculationAsync(y.Id, y.EndDate);
        Assert.False(calc.ControlsPassed);                                                     // klucz bez zatwierdzenia z metodologią
        Assert.Contains(calc.Notes, n => n.Contains("NIE jest zatwierdzony"));
        await new FinancialStatementApprovals(t.Db, t.User, t.Clock).ApproveCostKeyAsync(new CostAllocationKey(70m, 10m, 20m), "Ewidencja czasu pracy i powierzchni");
        calc = await rs.ProfitAndLossCalculationAsync(y.Id, y.EndDate);
        Assert.True(calc.ControlsPassed, string.Join("; ", calc.Notes));
        Assert.Contains(calc.Rows, r => (string?)r.Cells[0] == "O" && (decimal?)r.Cells[2] == 4277m);
        var eq = await rs.EquityChangesAsync(y.Id, y.EndDate);
        Assert.True(eq.ControlsPassed, string.Join("; ", eq.Notes));
        var cf = await rs.CashFlowAsync(y.Id, y.EndDate);
        Assert.True(cf.ControlsPassed, string.Join("; ", cf.Notes));

        var notes = await rs.NotesAsync(y.Id, y.EndDate);
        Assert.False(notes.ControlsPassed); // domyślne pola [DO UZUPEŁNIENIA]
        Assert.Contains(notes.Rows, r => (string?)r.Cells[1] == "Środki trwałe" && (decimal?)r.Cells[5] == 12000m);
        var store = new FinancialStatementNotes(new SettingsService(t.Db, t.Clock), t.Company.Id);
        var texts = await store.GetAsync(y.Name);
        texts["zatrudnienie"] = "Brak zatrudnienia na umowę o pracę.";
        texts["podzial-wyniku"] = "Zysk netto przeznacza się na kapitał zapasowy.";
        await store.SaveAsync(y.Name, texts);
        await t.Db.SaveChangesAsync();
        notes = await rs.NotesAsync(y.Id, y.EndDate);
        Assert.True(notes.ControlsPassed, string.Join("; ", notes.Notes));
        Assert.Contains(notes.Rows, r => (string?)r.Cells[1] == "Zysk netto przeznacza się na kapitał zapasowy.");
        foreach (var r in new[] { calc, eq, cf, notes })
            Assert.Equal("%PDF", Encoding.ASCII.GetString(new PdfReportExporter().Export(r), 0, 4));

        var sb = new StatementBuilder(t.Db);
        var company = await t.Db.Companies.AsNoTracking().SingleAsync();
        var pdf = new PdfReportExporter().Export(notes);
        var validator = new XsdValidator(Path.Combine(AppContext.BaseDirectory, "schemas"));
        var extras = new FinancialStatementXmlBuilder.Extras(null, null,
            await sb.EquityChangesAsync(y.Id, y.EndDate), null, await sb.CashFlowAsync(y.Id, y.EndDate), null, texts["kontynuacja"], texts["zasady"], pdf,
            AverageEmployment: 0m, IncomeTax: new FinancialStatementXmlBuilder.IncomeTaxInfo(4700m, 0, 0, 0, 0, 0, 0, 0, 0, 4700m, 423m));
        DomainException noPkd = Assert.Throws<DomainException>(() => FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate,
            new StatementValues { AsOf = y.EndDate }, null, new StatementValues { AsOf = y.EndDate }, null, validator, extras));
        Assert.Contains("PKD", noPkd.Message);
        company.PkdCode = "6920A"; // PKD 2025
        company.Voivodeship = "mazowieckie";
        company.County = "Warszawa";
        company.Commune = "Warszawa";
        var bsCur = await sb.BalanceSheetAsync(y.Id, y.EndDate);
        var plCur = await sb.ProfitAndLossAsync(y.Id, y.EndDate);
        // wariant porównawczy z zestawieniem zmian w kapitale, RPP, informacją dodatkową i informacją o podatku dochodowym
        var x = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator, extras);
        Assert.True(x.Validation.IsValid, string.Join(Environment.NewLine, x.Validation.Errors.Take(15)));
        var jin = FinancialStatementXmlBuilder.Jin;
        var dtsf = FinancialStatementXmlBuilder.Dtsf;
        var root = x.Document.Root!;
        Assert.Equal("17160.00", root.Descendants(jin + "Aktywa").Single().Element(dtsf + "KwotaA")!.Value);
        Assert.Equal("4277.00", root.Descendants(jin + "RZiSPor").Single().Element(jin + "L")!.Element(dtsf + "KwotaA")!.Value);
        Assert.Equal(pdf, Convert.FromBase64String(root.Descendants(dtsf + "Zawartosc").Single().Value));
        Assert.DoesNotContain(x.NotImplemented, n => n.StartsWith("Dodatkowe informacje"));
        // kompletność: z uzupełnionymi tekstami i zamkniętym rokiem — gotowe; brak informacji dodatkowej / pola „do uzupełnienia” / rok otwarty — wersja robocza
        Assert.Empty(FinancialStatementCompleteness.Check(bsCur, x, texts.Values, texts["kontynuacja"], texts["zasady"], yearClosed: true));
        var noNotes = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator, extras with { NotesPdf = null });
        var gaps = FinancialStatementCompleteness.Check(bsCur, noNotes, texts.Values.Append("[DO UZUPEŁNIENIA — test]"), texts["kontynuacja"], texts["zasady"], yearClosed: false);
        Assert.Contains(gaps, g => g.Contains("Dodatkowe informacje"));
        Assert.Contains(gaps, g => g.Contains("DO UZUPEŁNIENIA"));
        Assert.Contains(gaps, g => g.Contains("nie są zamknięte na dzień bilansowy"));
        // niepotwierdzona informacja dodatkowa, niezatwierdzony klucz kosztów, niezweryfikowany RPP — wersja robocza
        var approvalGaps = FinancialStatementCompleteness.Check(bsCur, x, texts.Values, texts["kontynuacja"], texts["zasady"], true, false, StatementScope.Inna,
            notesConfirmed: false, costKeyApproved: false, cashFlowVerified: false);
        Assert.Contains(approvalGaps, g => g.Contains("informacji dodatkowej") && g.Contains("potwierdzona"));
        Assert.Contains(approvalGaps, g => g.Contains("klucz podziału kosztów"));
        Assert.Contains(approvalGaps, g => g.Contains("Rachunek przepływów pieniężnych nie został zweryfikowany"));
        Assert.Contains(FinancialStatementCompleteness.Check(bsCur, x, texts.Values, texts["kontynuacja"], texts["zasady"], true, false, StatementScope.Inna, cashFlowUnreconciled: 12.5m),
            g => g.Contains("nie uzgadnia się"));
        Assert.DoesNotContain(FinancialStatementCompleteness.Check(bsCur, x, texts.Values, texts["kontynuacja"], texts["zasady"], true, false, StatementScope.Mala, cashFlowVerified: false),
            g => g.Contains("Rachunek przepływów pieniężnych"));

        // potwierdzenie treści informacji dodatkowej — zmiana tekstu je unieważnia
        var approvals = new FinancialStatementApprovals(t.Db, t.User, t.Clock);
        await approvals.ConfirmNotesAsync(y.Name, texts);
        Assert.True(await approvals.NotesConfirmedAsync(y.Name, texts));
        var changed = new Dictionary<string, string>(texts) { ["powiazane"] = "Pożyczka od akcjonariusza na warunkach rynkowych." };
        Assert.False(await approvals.NotesConfirmedAsync(y.Name, changed));
        await Assert.ThrowsAsync<DomainException>(() => approvals.ConfirmNotesAsync(y.Name, new Dictionary<string, string>(texts) { ["inne"] = "[DO UZUPEŁNIENIA]" }));
        // klucz podziału kosztów — tylko z metodologią; zmiana klucza wymaga ponownego zatwierdzenia
        await Assert.ThrowsAsync<DomainException>(() => approvals.ApproveCostKeyAsync(new CostAllocationKey(70, 10, 20), " "));
        await approvals.ApproveCostKeyAsync(new CostAllocationKey(70, 10, 20), "Ewidencja czasu pracy 2026");
        Assert.NotNull(await approvals.CostKeyApprovalAsync(new CostAllocationKey(70, 10, 20)));
        Assert.Null(await approvals.CostKeyApprovalAsync(CostAllocationKey.Default));
        // pełne sprawozdanie (jednostka inna niż mała/mikro) bez zestawienia zmian w kapitale i RPP — niekompletne nawet bez badania
        var noEquity = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator, extras with { Equity = null, CashFlow = null });
        var fullGaps = FinancialStatementCompleteness.Check(bsCur, noEquity, texts.Values, texts["kontynuacja"], texts["zasady"], true, auditRequired: false, StatementScope.Inna);
        Assert.Contains(fullGaps, g => g.Contains("Zestawienie zmian"));
        Assert.Contains(fullGaps, g => g.Contains("Rachunek przepływów"));
        // jednostka mała korzystająca z uproszczeń, niebadana — może pominąć oba
        Assert.DoesNotContain(FinancialStatementCompleteness.Check(bsCur, noEquity, texts.Values, texts["kontynuacja"], texts["zasady"], true, false, StatementScope.Mala),
            g => g.Contains("Zestawienie zmian") || g.Contains("Rachunek przepływów"));
        // wariant kalkulacyjny
        var xk = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator,
            extras with { CalculationVariant = await sb.ProfitAndLossCalculationAsync(y.Id, y.EndDate, CostAllocationKey.Default) });
        Assert.True(xk.Validation.IsValid, string.Join(Environment.NewLine, xk.Validation.Errors.Take(15)));
        Assert.Equal("4277.00", xk.Document.Root!.Descendants(jin + "RZiSKalk").Single().Element(jin + "O")!.Element(dtsf + "KwotaA")!.Value);

        // jednostka mała (SFJMAZ) — porównawczy i kalkulacyjny, bez zestawienia zmian w kapitale i RPP
        var small = extras with { Scope = StatementScope.Mala, Equity = null, CashFlow = null };
        var jma = FinancialStatementXmlBuilder.Jma;
        var xm = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator, small);
        Assert.True(xm.Validation.IsValid, string.Join(Environment.NewLine, xm.Validation.Errors.Take(15)));
        Assert.Equal("17160.00", xm.Document.Root!.Descendants(jma + "Aktywa").Single().Element(dtsf + "KwotaA")!.Value);
        Assert.Equal("4277.00", xm.Document.Root!.Descendants(jma + "RZiSPor").Single().Element(jma + "J")!.Element(dtsf + "KwotaA")!.Value);
        var xmk = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator,
            small with { CalculationVariant = await sb.ProfitAndLossCalculationAsync(y.Id, y.EndDate, CostAllocationKey.Default) });
        Assert.True(xmk.Validation.IsValid, string.Join(Environment.NewLine, xmk.Validation.Errors.Take(15)));
        Assert.Equal("4277.00", xmk.Document.Root!.Descendants(jma + "RZiSKalk").Single().Element(jma + "L")!.Element(dtsf + "KwotaA")!.Value);
        // jednostka mikro (SFJMIZ) — PKD nie jest wymagane
        company.PkdCode = null;
        var xi = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator, small with { Scope = StatementScope.Mikro });
        Assert.True(xi.Validation.IsValid, string.Join(Environment.NewLine, xi.Validation.Errors.Take(15)));
        var jmi = FinancialStatementXmlBuilder.Jmi;
        var rz = xi.Document.Root!.Descendants(FinancialStatementXmlBuilder.NsMikro + "RZiSJednostkaMikro").Single();
        decimal V(string n) => decimal.Parse(rz.Element(jmi + n)!.Element(dtsf + "KwotaA")!.Value, CultureInfo.InvariantCulture);
        Assert.Equal(4277m, V("F"));
        Assert.Equal(V("A") - V("B") + V("C") - V("D") - V("E"), V("F"));
        var ma = xi.Document.Root!.Descendants(jmi + "Aktywa").Single();
        Assert.Equal("17160.00", ma.Element(dtsf + "KwotaA")!.Value);

        // warianty w tysiącach złotych (SFJINT / SFJMAT / SFJMIT)
        company.PkdCode = "6920A";
        foreach (var scope in new[] { StatementScope.Inna, StatementScope.Mala, StatementScope.Mikro })
        {
            var tys = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator,
                (scope == StatementScope.Inna ? extras : small with { Scope = scope }) with { InThousands = true });
            Assert.True(tys.Validation.IsValid, scope + ": " + string.Join(Environment.NewLine, tys.Validation.Errors.Take(15)));
            Assert.Equal("17", tys.Document.Root!.Descendants().First(e => e.Name.LocalName == "Aktywa").Element(dtsf + "KwotaA")!.Value);
        }
    }

    private sealed class Fake(Func<string, (HttpStatusCode, string)> f) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r.RequestUri!.ToString());
            var (c, b) = f(r.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(c) { Content = new StringContent(b, Encoding.UTF8, "application/xml") });
        }
    }

    private const string MainXsd = """
        <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns:etd="urn:etd" elementFormDefault="qualified">
          <xsd:import namespace="urn:etd" schemaLocation="http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/StrukturyDanych_v10-0E.xsd"/>
          <xsd:element name="JPK" type="etd:TKod"/>
        </xsd:schema>
        """;

    private const string ImportedXsd = """
        <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:etd">
          <xsd:simpleType name="TKod"><xsd:restriction base="xsd:string"><xsd:maxLength value="5"/></xsd:restriction></xsd:simpleType>
        </xsd:schema>
        """;

    [Fact]
    public async Task Schema_library_downloads_imports_and_validates_offline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pk-xsd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fake = new Fake(u => u.EndsWith("schemat.xsd") ? (HttpStatusCode.OK, MainXsd) : u.EndsWith("StrukturyDanych_v10-0E.xsd") ? (HttpStatusCode.OK, ImportedXsd) : (HttpStatusCode.NotFound, ""));
            var lib = new SchemaLibrary(dir, new HttpClient(fake));
            Assert.False(lib.Status().Single(s => s.Schema.Name == "JPK_V7M(3)").Available);
            var files = await lib.DownloadAsync(SchemaLibrary.Known.Single(k => k.Name == "JPK_V7M(3)"));
            Assert.Equal(2, files.Count);
            Assert.True(lib.Status().Single(s => s.Schema.Name == "JPK_V7M(3)").Available);
            Assert.Empty(lib.MissingImports());

            var validator = new XsdValidator(dir);
            XNamespace t = "urn:t";
            Assert.True(validator.Validate(new XDocument(new XElement(t + "JPK", "abc")), PelnaKsiegowosc.Infrastructure.Jpk.JpkV7Rules.SchemaFile, "test").IsValid);
            Assert.False(validator.Validate(new XDocument(new XElement(t + "JPK", "abcdefgh")), PelnaKsiegowosc.Infrastructure.Jpk.JpkV7Rules.SchemaFile, "test").IsValid);

            await Assert.ThrowsAsync<InvalidOperationException>(() => lib.DownloadAsync(SchemaLibrary.Known.Single(k => k.Name.StartsWith("FA(3)")))); // schemat dołączony — bez adresu pobrania
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Schema_library_imports_zip_without_paths_and_reports_missing_imports()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pk-xsd-" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(Path.GetTempPath(), "pk-xsd-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("../../zly/" + FinancialStatementXmlBuilder.SchemaFile).Open())) w.Write(MainXsd);
                using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("x");
            }
            var lib = new SchemaLibrary(dir);
            var saved = lib.Import(new[] { zipPath });
            Assert.Equal(new[] { FinancialStatementXmlBuilder.SchemaFile }, saved);
            Assert.True(File.Exists(Path.Combine(dir, FinancialStatementXmlBuilder.SchemaFile)));
            Assert.Contains("StrukturyDanych_v10-0E.xsd", lib.MissingImports());
            var bad = Path.Combine(Path.GetTempPath(), "pk-bad-" + Guid.NewGuid().ToString("N") + ".xsd");
            File.WriteAllText(bad, "to nie jest xml");
            Assert.Throws<InvalidOperationException>(() => lib.Import(new[] { bad }));
            File.Delete(bad);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Declarations_cit8_vat_ue_foreign_payments_and_resolutions()
    {
        await using var t = await FullYearScenarioTests.BuildFullYearAsync(closeYear: true);
        var rs = new ReportService(t.Db, t.User, t.Clock);
        var cit = await rs.Cit8Async(t.Year.Id);
        Assert.Contains(cit.Rows, r => (string?)r.Cells[0] == "G.2" && (decimal?)r.Cells[2] == 423m);
        Assert.Contains(cit.Rows, r => (string?)r.Cells[0] == "D.1" && (decimal?)r.Cells[2] == 10000m);

        var ue = await rs.VatUeAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1));
        Assert.Equal(3, ue.Rows.Count(r => r.IsHeader));
        Assert.Contains(ue.Rows, r => (string?)r.Cells[3] == "brak transakcji");

        var ift = await rs.ForeignPaymentsAsync(2026);
        Assert.DoesNotContain(ift.Rows, r => !r.IsHeader && !r.IsTotal);

        var u = await rs.AnnualResolutionsAsync(t.Year.Id);
        Assert.False(u.ControlsPassed);
        Assert.Contains(u.Rows, r => ((string?)r.Cells[1] ?? "").Contains($"zysk netto w kwocie {Money.Format(4277m)} zł"));
        Assert.Contains(u.Rows, r => ((string?)r.Cells[1] ?? "").Contains(Money.Format(17160m)));
        foreach (var r in new[] { cit, ue, ift, u }) Assert.True(new PdfReportExporter().Export(r).Length > 100);
    }

    [Fact]
    public async Task Pit_partners_and_inventory_documents()
    {
        Assert.Equal(PitTaxForm.Liniowy, PitPartner.ParseLine("Jan Kowalski; 50; liniowy; 1000; 20000; 0").Form);
        Assert.Throws<DomainException>(() => PitPartner.ParseLine("Jan; 50; ryczałt"));
        Assert.Equal(0m, PitBusinessRules.Tax(30000m, PitTaxForm.Skala));
        Assert.Equal(10800m, PitBusinessRules.Tax(120000m, PitTaxForm.Skala));
        Assert.Equal(19000m, PitBusinessRules.Tax(100000m, PitTaxForm.Liniowy));

        await using var t = await FullYearScenarioTests.BuildFullYearAsync(closeYear: false);
        var rs = new ReportService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => rs.PitPartnersAsync(t.Year.Id, new[] { PitPartner.ParseLine("A; 60; skala") }));
        var pit = await rs.PitPartnersAsync(t.Year.Id, new[] { PitPartner.ParseLine("A; 50; skala"), PitPartner.ParseLine("B; 50; liniowy; 0; 20000; 100") });
        Assert.Equal(2, pit.Rows.Count);
        var b = pit.Rows[1];
        Assert.Equal(PitBusinessRules.LinearHealthDeductionLimit, b.Cells[7]); // odliczenie zdrowotnej ograniczone limitem

        var plan = await rs.InventoryPlanAsync(t.Year.Id, t.Year.EndDate);
        Assert.Contains(plan.Rows, r => (string?)r.Cells[0] == "130-01" && ((string?)r.Cells[4])!.StartsWith("potwierdzenie salda (bank)"));
        var conf = await rs.BalanceConfirmationsAsync(t.Year.EndDate, true);
        Assert.Contains(conf.Rows, r => ((string?)r.Cells[0] ?? "").StartsWith("Do: Dostawca"));
        var cash = await rs.CashCountAsync(t.Year.Id, t.Year.EndDate, "130-01", 7500m);
        Assert.False(cash.ControlsPassed);
        Assert.Equal(40m, cash.Rows.Single(r => (string?)r.Cells[0] == "3").Cells[2]);
    }
}

public class CashFlowGrossTests
{
    [Fact]
    public async Task Investing_purchase_and_sale_are_presented_separately_not_net()
    {
        await using var t = await PelnaKsiegowosc.Tests.Infrastructure.TestBooks.CreateAsync();
        var d = new DateOnly(2026, 4, 10);
        await t.Ledger().CreateAndPostAsync(PelnaKsiegowosc.Tests.Infrastructure.TestBooks.Entry(d, "PK/K", new("130-01", 200000m, 0), new("801", 0, 200000m)));
        // zakup środka trwałego za 100 000 zł (zapłata z rachunku) i sprzedaż innego za 80 000 zł (wpływ, przychód ze zbycia)
        await t.Ledger().CreateAndPostAsync(PelnaKsiegowosc.Tests.Infrastructure.TestBooks.Entry(d, "WB/1", new("010-03", 100000m, 0), new("130-01", 0, 100000m)));
        await t.Ledger().CreateAndPostAsync(PelnaKsiegowosc.Tests.Infrastructure.TestBooks.Entry(d, "WB/2", new("130-01", 80000m, 0), new("760-01", 0, 80000m)));
        await t.Db.SaveChangesAsync();
        var cf = await new StatementBuilder(t.Db).CashFlowAsync(t.Year.Id, t.Year.EndDate);
        Assert.Equal(80000m, cf.Get("B.I.1"));
        Assert.Equal(80000m, cf.Get("B.I"));
        Assert.Equal(100000m, cf.Get("B.II.1"));
        Assert.Equal(100000m, cf.Get("B.II"));
        Assert.Equal(-20000m, cf.Get("B.III"));
        Assert.Equal(200000m, cf.Get("C.I.1"));
        Assert.Equal(cf.Get("E"), cf.Get("D"));
    }

    [Fact]
    public async Task All_cash_flow_items_are_classified_without_netting_and_unreconciled_difference_is_not_hidden()
    {
        await using var t = await PelnaKsiegowosc.Tests.Infrastructure.TestBooks.CreateAsync();
        var chart = new PelnaKsiegowosc.Application.Ledger.ChartService(t.Db, t.User, t.Clock);
        await chart.AddAccountAsync(t.Chart.Id, "040", "Nieruchomości inwestycyjne", Domain.Accounts.AccountType.Aktywne, "A.IV.1");
        await chart.AddAccountAsync(t.Chart.Id, "134", "Kredyty bankowe", Domain.Accounts.AccountType.Pasywne, balanceCreditLine: "P.B.III.3.a");
        await chart.AddAccountAsync(t.Chart.Id, "241", "Obligacje wyemitowane", Domain.Accounts.AccountType.Pasywne, balanceCreditLine: "P.B.III.3.b");
        var leasing = await chart.AddAccountAsync(t.Chart.Id, "242", "Zobowiązania z tytułu leasingu finansowego", Domain.Accounts.AccountType.Pasywne, balanceCreditLine: "P.B.III.3.c");
        await chart.SetCashFlowCategoryAsync(leasing.Id, Domain.Accounts.CashFlowCategory.LeasingFinansowy);
        var d = new DateOnly(2026, 5, 4);
        async Task Post(string no, params PelnaKsiegowosc.Application.Ledger.LineInput[] lines)
        {
            await t.Ledger().CreateAndPostAsync(PelnaKsiegowosc.Tests.Infrastructure.TestBooks.Entry(d, no, lines));
            await t.Db.SaveChangesAsync();
        }
        await Post("PK/K", new("130-01", 500000m, 0), new("801", 0, 500000m));
        await Post("WB/1", new("040", 150000m, 0), new("130-01", 0, 150000m));                       // nieruchomość inwestycyjna — zakup
        await Post("WB/2", new("130-01", 90000m, 0), new("760-01", 0, 90000m));                        // zbycie środka trwałego
        await Post("WB/3", new("130-01", 100000m, 0), new("241", 0, 100000m));                         // emisja obligacji
        await Post("WB/4", new("241", 40000m, 0), new("130-01", 0, 40000m));                           // wykup obligacji
        await Post("WB/5", new("242", 12000m, 0), new("130-01", 0, 12000m));                           // rata leasingu finansowego
        // jeden zapis: wpływ kredytu 30 000 i zapłata za środek trwały 30 000 — bez kompensowania
        await Post("WB/6", new("130-01", 30000m, 0), new("134", 0, 30000m), new("010-03", 30000m, 0), new("130-01", 0, 30000m));
        await Post("WB/7", new("100", 5000m, 0), new("130-01", 0, 5000m));                              // przelew rachunek → kasa (wewnętrzny)
        var cf = await new StatementBuilder(t.Db).CashFlowAsync(t.Year.Id, t.Year.EndDate);
        Assert.Equal(150000m, cf.Get("B.II.2"));
        Assert.Equal(90000m, cf.Get("B.I.1"));
        Assert.Equal(30000m, cf.Get("B.II.1"));
        Assert.Equal(100000m, cf.Get("C.I.3"));
        Assert.Equal(40000m, cf.Get("C.II.5"));
        Assert.Equal(12000m, cf.Get("C.II.7"));
        Assert.Equal(30000m, cf.Get("C.I.2"));
        Assert.Equal(500000m, cf.Get("C.I.1"));
        Assert.Equal(0m, cf.Unreconciled);
        Assert.Equal(cf.Get("E"), cf.Get("D"));

        // różnice kursowe: wycena środków pieniężnych (+700, informacyjnie w E.1) i wycena kredytu walutowego (strata 500 — A.II.2)
        await Post("PK/FX1", new("130-01", 700m, 0), new("750-02", 0, 700m));
        await Post("PK/FX2", new("751-02", 500m, 0), new("134", 0, 500m));
        var fx = await new StatementBuilder(t.Db).CashFlowAsync(t.Year.Id, t.Year.EndDate);
        // KSR nr 1: wycena środków pieniężnych (+700) nie jest przepływem — wyłączona w A.II.2 i wykazana w E.1; strata z wyceny
        // kredytu (500) — także wyłączona; D = rzeczywiste przepływy, E = D + E.1;
        // KSR nr 1: G = F + D, może różnić się od salda bilansowego o wycenę walutową.
        Assert.Equal(700m, fx.Get("E.1"));
        Assert.Equal(-700m + 500m, fx.Get("A.II.2"));
        Assert.Equal(0m, fx.Unreconciled);
        Assert.Equal(fx.Get("D") + 700m, fx.Get("E"));
        Assert.Equal(fx.Get("F") + fx.Get("D"), fx.Get("G"));
        Assert.Equal(fx.Get("G") + fx.Get("E.1"), fx.Get("F") + fx.Get("E"));
        Assert.Equal(30000m, fx.Get("C.I.2"));                                                   // wycena kredytu nie jest wpływem

        // wpływ zaksięgowany na konto bez pozycji sprawozdania — różnica uzgodnienia jest jawna, nie „dopisywana” automatycznie
        await chart.AddAccountAsync(t.Chart.Id, "249", "Rozliczenia do wyjaśnienia", Domain.Accounts.AccountType.AktywnoPasywne);
        await Post("WB/8", new("130-01", 700m, 0), new("249", 0, 700m));
        var unreconciled = await new StatementBuilder(t.Db).CashFlowAsync(t.Year.Id, t.Year.EndDate);
        Assert.Equal(0m, unreconciled.Get("A.II.10"));
        Assert.Equal(700m, unreconciled.Unreconciled);
        Assert.Contains(unreconciled.Warnings, w => w.Contains("NIEUZGODNIONE"));
        var withAdjustment = await new StatementBuilder(t.Db).CashFlowAsync(t.Year.Id, t.Year.EndDate, default, new[] { ("Wpływ do wyjaśnienia — zaliczka (konto 249)", 700m) });
        Assert.Equal(0m, withAdjustment.Unreconciled);

        // weryfikacja RPP dotyczy konkretnej wersji wyliczenia — kolejny zapis unieważnia potwierdzenie
        var approvals = new FinancialStatementApprovals(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => approvals.VerifyCashFlowAsync("2026", "Sprawdzono", unreconciled)); // nieuzgodniony — nie można potwierdzić
        await approvals.VerifyCashFlowAsync("2026", "Sprawdzono klasyfikację kont i korekty", withAdjustment);
        Assert.True(await approvals.CashFlowVerifiedAsync("2026", withAdjustment));
        await Post("WB/9", new("010-03", 1000m, 0), new("130-01", 0, 1000m));
        var changed = await new StatementBuilder(t.Db).CashFlowAsync(t.Year.Id, t.Year.EndDate, default, new[] { ("Wpływ do wyjaśnienia — zaliczka (konto 249)", 700m) });
        Assert.False(await approvals.CashFlowVerifiedAsync("2026", changed));
        Assert.Single(await approvals.CashFlowVerificationsAsync("2026"));
        Assert.Equal(700m, withAdjustment.Get("A.II.10"));
    }

    [Fact]
    public async Task Realized_exchange_differences_on_settlement_and_foreign_loan_repayment_are_presented_per_ksr1()
    {
        await using var t = await PelnaKsiegowosc.Tests.Infrastructure.TestBooks.CreateAsync();
        var chart = new PelnaKsiegowosc.Application.Ledger.ChartService(t.Db, t.User, t.Clock);
        await chart.AddAccountAsync(t.Chart.Id, "134", "Kredyty bankowe", Domain.Accounts.AccountType.Pasywne, balanceCreditLine: "P.B.III.3.a");
        var cp = await t.AddCounterpartyAsync("ODB", "Odbiorca zagraniczny");
        async Task Post(DateOnly d, string no, params PelnaKsiegowosc.Application.Ledger.LineInput[] lines)
        {
            await t.Ledger().CreateAndPostAsync(PelnaKsiegowosc.Tests.Infrastructure.TestBooks.Entry(d, no, lines));
            await t.Db.SaveChangesAsync();
        }
        await Post(new DateOnly(2026, 3, 1), "PK/K", new("130-01", 50000m, 0), new("801", 0, 50000m));
        await Post(new DateOnly(2026, 3, 2), "FV/1", new("200", 4300m, 0, cp.Id), new("700-01", 0, 4300m));       // 1000 EUR × 4,30
        // zapłata 1000 EUR × 4,40: zrealizowana dodatnia różnica kursowa 100 — przepływ operacyjny w kwocie faktycznie otrzymanej
        await Post(new DateOnly(2026, 4, 2), "WB/1", new("130-01", 4400m, 0), new("200", 0, 4300m, cp.Id), new("750-02", 0, 100m));
        // kredyt walutowy 10 000 EUR × 4,00 i spłata po 4,10 — zrealizowana ujemna różnica 1 000
        await Post(new DateOnly(2026, 4, 3), "WB/2", new("130-01", 40000m, 0), new("134", 0, 40000m));
        await Post(new DateOnly(2026, 9, 3), "WB/3", new("134", 40000m, 0), new("751-02", 1000m, 0), new("130-01", 0, 41000m));
        var cf = await new StatementBuilder(t.Db).CashFlowAsync(t.Year.Id, t.Year.EndDate);
        Assert.Equal(0m, cf.Unreconciled);
        Assert.Equal(0m, cf.Get("E.1"));                                                        // brak wyceny środków pieniężnych
        Assert.Equal(40000m, cf.Get("C.I.2"));
        Assert.Equal(cf.Get("D"), cf.Get("E"));
        Assert.Equal(41000m, cf.Get("C.II.4"));                                                 // spłata w faktycznie zapłaconej kwocie
        Assert.Equal(1000m, cf.Get("A.II.2"));                                                  // zrealizowana strata kursowa z działalności finansowej
        Assert.Equal(50000m + 4400m + 40000m - 41000m, cf.Get("G"));                            // stan bilansowy środków
        Assert.Equal(cf.Get("F") + cf.Get("E"), cf.Get("G"));
        Assert.Equal(cf.Get("A.III") + cf.Get("B.III") + cf.Get("C.III"), cf.Get("D"));
        // operacyjne = rzeczywisty wpływ od odbiorcy (4 400), finansowe = faktycznie zapłacona kwota spłaty
        Assert.Equal(4400m, cf.Get("A.III"));
        Assert.Equal(40000m + 50000m - 41000m, cf.Get("C.III"));
    }
}
