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
        Assert.Equal(-12000m, cf.Get("B.IV"));
        Assert.Equal(10000m, cf.Get("C.IV"));
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
        // wariant kalkulacyjny
        var xk = FinancialStatementXmlBuilder.Build(company, y.StartDate, y.EndDate, y.EndDate, bsCur, null, plCur, null, validator,
            extras with { CalculationVariant = await sb.ProfitAndLossCalculationAsync(y.Id, y.EndDate, CostAllocationKey.Default) });
        Assert.True(xk.Validation.IsValid, string.Join(Environment.NewLine, xk.Validation.Errors.Take(15)));
        Assert.Equal("4277.00", xk.Document.Root!.Descendants(jin + "RZiSKalk").Single().Element(jin + "O")!.Element(dtsf + "KwotaA")!.Value);
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
