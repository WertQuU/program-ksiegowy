using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Application.Capital;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Periods;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Export;
using PelnaKsiegowosc.Tests.Infrastructure;
using PelnaKsiegowosc.Tests.Modules;

namespace PelnaKsiegowosc.Tests.Integration;

/// <summary>Scenariusz pełnego roku obrotowego P.S.A.: od objęcia akcji po bilans otwarcia kolejnego roku.</summary>
public class FullYearScenarioTests
{
    internal static async Task<TestBooks> BuildFullYearAsync(bool closeYear)
    {
        var t = await TestBooks.CreateAsync();
        var cap = new CapitalService(t.Db, t.User, t.Clock);
        var sh = await cap.AddShareholderAsync("Anna Założycielka", null, false);
        var sub = await cap.CreateEventAsync(new CapitalEvent
        {
            Type = CapitalEventType.ObjecieAkcjiWkladPieniezny, EventDate = new DateOnly(2026, 1, 2), ShareholderId = sh.Id, ShareCount = 1000,
            ShareCapitalAmount = 1000m, SupplementaryCapitalAmount = 9000m, ContributionType = ContributionType.Pieniezny,
            AgreementReference = "Umowa spółki z 02.01.2026", Description = "Objęcie akcji serii A"
        });
        await cap.ConfirmDocumentsAsync(sub.Id);
        await cap.PostEventAsync(sub.Id);
        var pay = await cap.CreateEventAsync(new CapitalEvent { Type = CapitalEventType.WplataWkladu, EventDate = new DateOnly(2026, 1, 3), ShareholderId = sh.Id, TotalAmount = 10000m });
        await cap.ConfirmDocumentsAsync(pay.Id);
        await cap.PostEventAsync(pay.Id);

        var client = await t.AddCounterpartyAsync("K001", "Klient sp. z o.o.", "7740001454");
        var supplier = await t.AddCounterpartyAsync("D001", "Dostawca S.A.", "5260250274");
        var inv = await SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(t, client.Id, 10000m, new DateOnly(2026, 1, 20));
        var purchase = await PurchaseScenarioTests.RegisterPurchaseAsync(t, supplier.Id, 2000m, "FV/123/2026", 100m);
        // termin płatności uzgodniony na 15.01.2027 — zobowiązanie nie podlega jeszcze art. 18f ustawy o CIT (test osobny)
        (await t.Db.OpenItems.SingleAsync(o => o.DocumentId == purchase.Id)).DueDate = new DateOnly(2027, 1, 15);
        await t.Db.SaveChangesAsync();

        // zakup środka trwałego na 080 + OT
        var docs = new DocumentService(t.Db, t.User, t.Clock);
        var d = new DateOnly(2026, 1, 25);
        var assetInv = await docs.CreateInvoiceAsync(new InvoiceInput(DocumentKind.FakturaZakupu, supplier.Id, d, d,
            new[] { new DocumentLineInput("Serwer", 1, 12000m, "23", AccountNumber: "080") }, ExternalNumber: "FV/124/2026", ReceiptDate: d));
        await docs.SetVatDeductionAsync(assetInv.Id, new Dictionary<string, (decimal, bool)> { ["23"] = (100m, true) }, new DateOnly(2026, 1, 1));
        await docs.ApproveAsync(assetInv.Id);
        await docs.PostAsync(assetInv.Id);
        var fa = new FixedAssetService(t.Db, t.User, t.Clock);
        await fa.RegisterAsync(new FixedAsset
        {
            InventoryNumber = "ST/2026/1", Name = "Serwer", KstSymbol = "491", AcquisitionDate = d, PlacedInServiceDate = d,
            InitialValueBook = 12000m, InitialValueTax = 12000m, BookRatePercent = 30m, TaxRatePercent = 30m,
            AssetAccountNumber = "010-03", AccumulatedDepreciationAccountNumber = "070-03", ExpenseAccountNumber = "400"
        }, "080");
        // zapłata za środek trwały
        var assetItem = await t.Db.OpenItems.SingleAsync(o => o.Reference.Contains("FV/124/2026") || o.DocumentId == assetInv.Id);
        var payEntry = await t.Ledger().CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 28), "WB/1/2026", new("210", 14760m, 0, supplier.Id, OpenItemId: assetItem.Id), new("130-01", 0, 14760m)));
        await t.Ledger().PostAsync(payEntry.Id);

        // wpłata klienta przez import wyciągu
        var bank = new BankService(t.Db, t.User, t.Clock);
        var acc = await bank.CreateMoneyAccountAsync(MoneyAccountKind.RachunekBankowy, "Rachunek bieżący", "130-01", null);
        var csv = Encoding.UTF8.GetBytes($"Data;Kwota;Kontrahent;Rachunek;Tytul;Ref\n2026-02-05;12300,00;Klient sp. z o.o.;;Zaplata za {inv.Number};X1\n");
        var imp = await bank.ImportStatementAsync(acc.Id, CsvStatementParser.Parse(csv, new CsvImportProfile()), "luty.csv", csv);
        Assert.Equal(1, await bank.AutoMatchAsync(imp.StatementId));
        await bank.PostTransactionAsync((await t.Db.MoneyTransactions.SingleAsync()).Id);

        await new VatService(t.Db, t.User, t.Clock).PostSettlementAsync(new DateOnly(2026, 1, 1));

        for (var m = 2; m <= 12; m++) await fa.RunDepreciationAsync(2026, m);

        // podatek dochodowy za rok
        var cit = await new CitService(t.Db, t.User, t.Clock).ReconcileAsync(t.Year.Id, 12);
        var taxEntry = await t.Ledger().CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 12, 31), "PK/CIT/2026", new("870", cit.Computation.Tax, 0), new("220-01", 0, cit.Computation.Tax)));
        await t.Ledger().PostAsync(taxEntry.Id);

        var periods = new PeriodService(t.Db, t.User, t.Clock);
        foreach (var p in t.Year.Periods.OrderBy(p => p.Number)) await periods.ClosePeriodAsync(p.Id, "Kontrola okresu wykonana — test scenariuszowy");
        if (closeYear) await periods.CloseYearAsync(t.Year.Id, "ZAMKNIJ 2026", "Wyliczenia CIT zweryfikowane w uzgodnieniu wyniku");
        return t;
    }

    [Fact]
    public async Task Full_year_produces_balanced_statements_and_correct_opening_balance()
    {
        await using var t = await BuildFullYearAsync(closeYear: false);
        var sb = new StatementBuilder(t.Db);
        var pl = await sb.ProfitAndLossAsync(t.Year.Id, t.Year.EndDate);
        Assert.Equal(10000m, pl.Get("R.A"));
        Assert.Equal(3300m, pl.Get("R.B.I"));
        Assert.Equal(2000m, pl.Get("R.B.III"));
        Assert.Equal(4700m, pl.Get("R.C"));
        Assert.Equal(4700m, pl.Get("R.I"));
        Assert.Equal(423m, pl.Get("R.J")); // 9% × 4 700
        Assert.Equal(4277m, pl.Get("R.L"));

        var bs = await sb.BalanceSheetAsync(t.Year.Id, t.Year.EndDate);
        Assert.Empty(bs.Warnings);
        Assert.Equal(8700m, bs.Get("A.II.1.c"));
        Assert.Equal(7540m, bs.Get("B.III.1.c"));
        Assert.Equal(920m, bs.Get("B.II.3.b"));
        Assert.Equal(17160m, bs.Get("AKTYWA"));
        Assert.Equal(1000m, bs.Get("P.A.I"));
        Assert.Equal(9000m, bs.Get("P.A.II"));
        Assert.Equal(4277m, bs.Get("P.A.VI"));
        Assert.Equal(2460m, bs.Get("P.B.III.3.d"));
        Assert.Equal(423m, bs.Get("P.B.III.3.g"));
        Assert.Equal(17160m, bs.Get("PASYWA"));

        // zamknięcie roku
        var periods = new PeriodService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => periods.CloseYearAsync(t.Year.Id, "zamknij", "x"));
        var next = await periods.CloseYearAsync(t.Year.Id, "ZAMKNIJ 2026", "CIT zweryfikowany");
        t.Db.ChangeTracker.Clear();
        var closed = await t.Db.FiscalYears.SingleAsync(f => f.Id == t.Year.Id);
        Assert.Equal(FiscalYearStatus.Zamkniety, closed.Status);
        Assert.Equal("2027", next.Name);
        Assert.True(next.OpeningBalanceApproved);

        // po zamknięciu: konta wynikowe zerowe, wynik na 860, RZiS bez zmian (zapisy zamknięcia pomijane)
        Assert.Equal(0m, await t.BalanceAsync("700-01"));
        Assert.Equal(-4277m, await t.BalanceAsync("860"));
        var plAfter = await sb.ProfitAndLossAsync(t.Year.Id, t.Year.EndDate);
        Assert.Equal(4277m, plAfter.Get("R.L"));
        var bsAfter = await sb.BalanceSheetAsync(t.Year.Id, t.Year.EndDate);
        Assert.Equal(17160m, bsAfter.Get("PASYWA"));
        Assert.Equal(4277m, bsAfter.Get("P.A.VI"));

        // BO 2027
        var bo = await sb.BalanceSheetAsync(next.Id, next.StartDate);
        Assert.Equal(17160m, bo.Get("AKTYWA"));
        Assert.Equal(17160m, bo.Get("PASYWA"));
        Assert.Equal(4277m, bo.Get("P.A.V")); // wynik z lat ubiegłych (820)
        Assert.Equal(0m, bo.Get("P.A.VI"));
        Assert.Empty(bo.Warnings);

        // rok zamknięty: brak księgowania
        var lateEntry = new LedgerService(t.Db, t.User, t.Clock);
        await Assert.ThrowsAsync<DomainException>(() => lateEntry.CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 12, 30), "PK/LATE", new("130-01", 1m, 0), new("801", 0, 1m)))
            .ContinueWith(async tsk => await lateEntry.PostAsync(tsk.Result.Id)).Unwrap());

        // rozrachunek z 2026 rozliczany w 2027 — konto nowego planu kont, ta sama pozycja rozrachunkowa
        var openPayable = await t.Db.OpenItems.SingleAsync(o => o.Side == OpenItemSide.Zobowiazanie && o.Status != OpenItemStatus.Rozliczona);
        Assert.Equal(2460m, openPayable.RemainingPln);
        t.Db.ChangeTracker.Clear();
        var ledger2027 = new LedgerService(t.Db, t.User, t.Clock);
        var p27 = await ledger2027.CreateDraftAsync(TestBooks.Entry(new DateOnly(2027, 1, 10), "WB/1/2027", new("210", 2460m, 0, openPayable.CounterpartyId, OpenItemId: openPayable.Id), new("130-01", 0, 2460m)));
        await ledger2027.PostAsync(p27.Id);
        Assert.Equal(OpenItemStatus.Rozliczona, (await t.Db.OpenItems.AsNoTracking().SingleAsync(o => o.Id == openPayable.Id)).Status);

        var integrity = await new IntegrityService(t.Db, t.Clock).FullCheckAsync(t.Company.Id);
        Assert.True(integrity.AllOk, string.Join(Environment.NewLine, integrity.Results.Where(r => !r.Ok).Select(r => r.Area + ": " + r.Details)));
        Assert.Contains(integrity.Results, r => r.Area.StartsWith("Ciągłość BO 2027"));
    }

    [Fact]
    public async Task All_reports_are_generated_from_ledger_and_exported_to_pdf_xlsx_csv()
    {
        await using var t = await BuildFullYearAsync(closeYear: true);
        var rs = new ReportService(t.Db, t.User, t.Clock);
        var y = t.Year;
        var reports = new List<ReportDocument>
        {
            await rs.JournalAsync(y.Id, y.StartDate, y.EndDate),
            await rs.GeneralLedgerAsync(y.Id, "130", y.StartDate, y.EndDate),
            await rs.TrialBalanceAsync(y.Id, new DateOnly(2026, 12, 1), y.EndDate),
            await rs.SubsidiaryBalancesAsync(y.Id, y.EndDate),
            await rs.BalanceSheetAsync(y.Id, y.EndDate),
            await rs.ProfitAndLossAsync(y.Id, y.EndDate),
            await rs.VatRegisterAsync(VatRegisterType.Sprzedaz, new DateOnly(2026, 1, 1)),
            await rs.VatRegisterAsync(VatRegisterType.Zakup, new DateOnly(2026, 1, 1)),
            await rs.AgingAsync(OpenItemSide.Zobowiazanie, y.EndDate, false),
            await rs.AgingAsync(OpenItemSide.Naleznosc, y.EndDate, true),
            await rs.FixedAssetsAsync(y.EndDate),
            await rs.DepreciationScheduleAsync(2026, DepreciationBasis.Bilansowa),
            await rs.DepreciationScheduleAsync(2026, DepreciationBasis.Podatkowa),
            await rs.BankReconciliationAsync(y.Id, y.EndDate),
            await rs.ChangeLogAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)),
            await rs.PeriodCloseReportAsync(y.Periods.Single(p => p.Number == 12).Id),
            await rs.IntegrityReportAsync(),
            await rs.CitReconciliationAsync(y.Id, 12),
            await rs.SystemDescriptionAsync("SQLite: test"),
            await rs.OpeningBalanceAsync((await t.Db.FiscalYears.SingleAsync(f => f.Name == "2027")).Id)
        };

        var journal = reports[0];
        Assert.True(journal.ControlsPassed);
        Assert.Equal(journal.Rows[^1].Cells[7], journal.Rows[^1].Cells[8]);
        var tbReport = reports[2];
        Assert.True(tbReport.ControlsPassed);
        var bsReport = reports[4];
        Assert.True(bsReport.ControlsPassed, string.Join("; ", bsReport.Notes));
        var total = bsReport.Rows.Where(r => r.IsTotal).Select(r => (decimal)r.Cells[2]!).ToList();
        Assert.Equal(total[0], total[1]);
        Assert.True(reports[5].ControlsPassed, string.Join("; ", reports[5].Notes));
        Assert.Equal("OK", reports[16].Rows.First().Cells[1]);
        Assert.Single(reports[11].Rows, r => !r.IsTotal); // 1 składnik majątku

        var exporters = new IReportExporter[] { new PdfReportExporter(), new XlsxReportExporter(), new CsvReportExporter() };
        var dir = Path.Combine(t.Directory, "eksport");
        Directory.CreateDirectory(dir);
        foreach (var r in reports)
        foreach (var e in exporters)
        {
            var bytes = e.Export(r);
            Assert.True(bytes.Length > 100, $"{r.Title} {e.Format}");
            File.WriteAllBytes(Path.Combine(dir, $"{reports.IndexOf(r):00}{e.FileExtension}"), bytes);
        }
        var pdf = new PdfReportExporter().Export(bsReport);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        using (var wb = new XLWorkbook(new MemoryStream(new XlsxReportExporter().Export(bsReport))))
        {
            var ws = wb.Worksheets.First();
            Assert.Contains("Bilans", ws.Cell(2, 1).GetString());
            Assert.Contains(ws.CellsUsed(), c => c.DataType == XLDataType.Number && c.GetValue<decimal>() == 17160m);
        }
        var csv = Encoding.UTF8.GetString(new CsvReportExporter().Export(bsReport));
        Assert.Contains("Waluta: PLN", csv);
        Assert.Contains("17160,00", csv);
        Assert.Contains("Okres:", csv);
    }
}
