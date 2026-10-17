using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Application.Cit;
using PelnaKsiegowosc.Application.FixedAssets;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Periods;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.FixedAssets;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Reporting;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Domain.Vat;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>Generator raportów księgowych. Każdy raport powstaje z bieżących zapisów — brak wartości stałych/przykładowych.</summary>
public sealed class ReportService
{
    private readonly KsiegiDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public ReportService(KsiegiDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    private async Task<ReportDocument> NewAsync(string title, string period, bool landscape = false, CancellationToken ct = default)
    {
        _user.Require(Permission.RunReports);
        var c = await _db.Companies.AsNoTracking().FirstAsync(x => x.Id == _user.CompanyId, ct);
        return new ReportDocument
        {
            Title = title,
            CompanyName = c.FullName,
            CompanyNip = c.Nip,
            PeriodDescription = period,
            GeneratedAtUtc = _clock.UtcNow,
            GeneratedBy = _user.Login,
            Landscape = landscape
        };
    }

    private async Task<FiscalYear> YearAsync(Guid id, CancellationToken ct) =>
        await _db.FiscalYears.AsNoTracking().Include(f => f.Periods).FirstAsync(f => f.Id == id && f.CompanyId == _user.CompanyId, ct);

    /// <summary>Dziennik (art. 14 UoR): zapisy w kolejności chronologicznej, obroty okresu i narastająco od początku roku.</summary>
    public async Task<ReportDocument> JournalAsync(Guid fiscalYearId, DateOnly from, DateOnly to, string? journalCode = null, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var r = await NewAsync("Dziennik", $"{from.ToPl()} – {to.ToPl()} (rok obrotowy {fy.Name})", true, ct);
        r.Filters.Add(("Dziennik częściowy", journalCode ?? "wszystkie"));
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Poz.", ColumnKind.Integer, 0.5), new ReportColumn("Nr w dzienniku", ColumnKind.Text, 1.3), new ReportColumn("Data księg.", ColumnKind.Date, 0.8),
            new ReportColumn("Data oper.", ColumnKind.Date, 0.8), new ReportColumn("Dowód", ColumnKind.Text, 1.2), new ReportColumn("Opis", ColumnKind.Text, 2.6),
            new ReportColumn("Konto", ColumnKind.Text, 0.8), new ReportColumn("Wn", ColumnKind.Money, 1), new ReportColumn("Ma", ColumnKind.Money, 1)
        });
        var all = await new LedgerQueries(_db).LinesAsync(fiscalYearId, null, to, true, null, ct);
        decimal pd = 0, pc = 0, cd = 0, cc = 0;
        foreach (var g in all.GroupBy(l => l.EntryId).OrderBy(g => g.First().Sequence))
        {
            var f = g.First();
            var inScope = f.PostingDate >= from && (journalCode is null || f.JournalCode == journalCode);
            foreach (var l in g)
            {
                cd += l.Debit; cc += l.Credit;
                if (!inScope) continue;
                pd += l.Debit; pc += l.Credit;
            }
            if (!inScope) continue;
            var first = true;
            foreach (var l in g)
            {
                r.Add(first ? f.Sequence : null, first ? f.JournalNumber : null, first ? f.PostingDate : null, first ? f.OperationDate : null,
                    first ? f.DocumentNumber : null, first ? f.Description : null, l.AccountNumber, l.Debit == 0 ? null : l.Debit, l.Credit == 0 ? null : l.Credit);
                first = false;
            }
        }
        r.AddRow(new ReportRow(null, null, null, null, null, "Obroty za okres", null, pd, pc) { IsTotal = true });
        if (journalCode is null)
            r.AddRow(new ReportRow(null, null, null, null, null, "Obroty narastająco od początku roku obrotowego", null, cd, cc) { IsTotal = true });
        r.ControlsPassed = pd == pc;
        r.Notes.Add("Każdy zapis zawiera identyfikator osoby zatwierdzającej w dzienniku audytowym (art. 23 ust. 3 UoR).");
        return r;
    }

    /// <summary>Księga główna — konto: BO, zapisy, salda narastająco.</summary>
    public async Task<ReportDocument> GeneralLedgerAsync(Guid fiscalYearId, string accountNumber, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var chart = await _db.Charts.AsNoTracking().Where(c => c.FiscalYearId == fiscalYearId).OrderByDescending(c => c.Version).FirstAsync(ct);
        var accs = await _db.Accounts.AsNoTracking().Where(a => a.ChartId == chart.Id).ToListAsync(ct);
        var acc = accs.FirstOrDefault(a => a.Number == accountNumber) ?? throw new DomainException($"Brak konta {accountNumber}.");
        var ids = accs.Where(a => a.Number == accountNumber || a.Number.StartsWith(accountNumber + "-")).Select(a => a.Id).ToList();
        var r = await NewAsync($"Księga główna — konto {acc.Number} {acc.Name}", $"{from.ToPl()} – {to.ToPl()}", true, ct);
        r.Filters.Add(("Konto", $"{acc.Number} (z analityką)"));
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Data", ColumnKind.Date, 0.8), new ReportColumn("Nr w dzienniku", ColumnKind.Text, 1.3), new ReportColumn("Dowód", ColumnKind.Text, 1.2),
            new ReportColumn("Opis", ColumnKind.Text, 2.5), new ReportColumn("Konto", ColumnKind.Text, 0.8), new ReportColumn("Wn", ColumnKind.Money), new ReportColumn("Ma", ColumnKind.Money),
            new ReportColumn("Saldo", ColumnKind.Money)
        });
        var lines = await new LedgerQueries(_db).LinesAsync(fiscalYearId, null, to, true, ids, ct);
        var before = lines.Where(l => l.PostingDate < from || l.Kind == EntryKind.BilansOtwarcia).ToList();
        var bo = before.Sum(l => l.Debit - l.Credit);
        r.AddRow(new ReportRow(from, null, null, "Saldo początkowe (BO i obroty przed okresem)", null, before.Sum(l => l.Debit), before.Sum(l => l.Credit), bo) { IsTotal = true });
        var running = bo;
        decimal td = 0, tc = 0;
        foreach (var l in lines.Except(before))
        {
            running += l.Debit - l.Credit;
            td += l.Debit; tc += l.Credit;
            r.Add(l.PostingDate, l.JournalNumber, l.DocumentNumber, l.Description, l.AccountNumber, l.Debit == 0 ? null : l.Debit, l.Credit == 0 ? null : l.Credit, running);
        }
        r.AddRow(new ReportRow(null, null, null, "Obroty okresu", null, td, tc, null) { IsTotal = true });
        r.AddRow(new ReportRow(to, null, null, "Saldo końcowe", null, null, null, running) { IsTotal = true });
        return r;
    }

    /// <summary>Zestawienie obrotów i sald (art. 18 ust. 1 UoR).</summary>
    public async Task<ReportDocument> TrialBalanceAsync(Guid fiscalYearId, DateOnly periodFrom, DateOnly periodTo, bool syntheticOnly = false, bool onlyWithActivity = true, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var r = await NewAsync("Zestawienie obrotów i sald", $"okres {periodFrom.ToPl()} – {periodTo.ToPl()}; narastająco od {fy.StartDate.ToPl()}", true, ct);
        r.Filters.Add(("Poziom kont", syntheticOnly ? "syntetyczne" : "wszystkie"));
        r.Filters.Add(("Pomijaj konta bez obrotów", onlyWithActivity ? "tak" : "nie"));
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Konto", ColumnKind.Text, 0.9), new ReportColumn("Nazwa", ColumnKind.Text, 2.4),
            new ReportColumn("BO Wn", ColumnKind.Money), new ReportColumn("BO Ma", ColumnKind.Money),
            new ReportColumn("Obroty okresu Wn", ColumnKind.Money), new ReportColumn("Obroty okresu Ma", ColumnKind.Money),
            new ReportColumn("Obroty narast. Wn", ColumnKind.Money), new ReportColumn("Obroty narast. Ma", ColumnKind.Money),
            new ReportColumn("Saldo Wn", ColumnKind.Money), new ReportColumn("Saldo Ma", ColumnKind.Money)
        });
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, periodFrom, periodTo, ct);
        foreach (var t in tb)
        {
            if (syntheticOnly && t.Account.Level > 0) continue;
            if (onlyWithActivity && !t.HasActivity) continue;
            r.AddRow(new ReportRow(t.Account.Number, t.Account.Name, t.OpeningDebit, t.OpeningCredit, t.PeriodDebit, t.PeriodCredit, t.CumulativeDebit, t.CumulativeCredit,
                t.BalanceDebit, t.BalanceCredit) { Level = t.Account.Level, IsHeader = !t.IsLeaf });
        }
        var leaves = tb.Where(t => t.IsLeaf && t.Account.Type != Domain.Accounts.AccountType.Pozabilansowe).ToList();
        r.AddRow(new ReportRow("RAZEM", "konta bilansowe i wynikowe (poziom najniższy)", leaves.Sum(t => t.OpeningDebit), leaves.Sum(t => t.OpeningCredit),
            leaves.Sum(t => t.PeriodDebit), leaves.Sum(t => t.PeriodCredit), leaves.Sum(t => t.CumulativeDebit), leaves.Sum(t => t.CumulativeCredit),
            leaves.Sum(t => t.BalanceDebit), leaves.Sum(t => t.BalanceCredit)) { IsTotal = true });
        r.ControlsPassed = leaves.Sum(t => t.PeriodDebit) == leaves.Sum(t => t.PeriodCredit) && leaves.Sum(t => t.Balance) == 0;
        r.Notes.Add("Saldo kont aktywno-pasywnych o charakterze rozrachunkowym — patrz zestawienie sald kont pomocniczych (rozrachunki per kontrahent).");
        if (!r.ControlsPassed) r.Notes.Add("UWAGA: obroty Wn ≠ Ma lub suma sald ≠ 0 — uruchom kontrolę integralności.");
        var journal = (await new LedgerQueries(_db).LinesAsync(fiscalYearId, periodFrom, periodTo, false, null, ct)).Sum(l => l.Debit);
        r.Notes.Add($"Kontrola: obroty dziennika za okres (bez BO) = {Money.Format(journal)}; obroty ZOiS (Wn) = {Money.Format(leaves.Sum(t => t.PeriodDebit) + tb.Where(t => t.IsLeaf && t.Account.Type == Domain.Accounts.AccountType.Pozabilansowe).Sum(t => t.PeriodDebit))}.");
        return r;
    }

    /// <summary>Zestawienie sald kont pomocniczych — rozrachunki per kontrahent (art. 18 ust. 2 UoR).</summary>
    public async Task<ReportDocument> SubsidiaryBalancesAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        await YearAsync(fiscalYearId, ct);
        var r = await NewAsync("Zestawienie sald kont pomocniczych (rozrachunki)", $"na dzień {asOf.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Konto"), new ReportColumn("Kontrahent", ColumnKind.Text, 2.5), new ReportColumn("Saldo Wn", ColumnKind.Money), new ReportColumn("Saldo Ma", ColumnKind.Money) });
        var balances = await new LedgerQueries(_db).BalancesByCounterpartyAsync(fiscalYearId, asOf, ct);
        var names = await _db.Counterparties.AsNoTracking().Where(c => c.CompanyId == _user.CompanyId).ToDictionaryAsync(c => c.Id, c => $"{c.Code} {c.Name}", ct);
        var chart = await _db.Charts.AsNoTracking().Where(c => c.FiscalYearId == fiscalYearId).OrderByDescending(c => c.Version).FirstAsync(ct);
        var accs = await _db.Accounts.AsNoTracking().Where(a => a.ChartId == chart.Id && a.IsSettlementAccount).ToDictionaryAsync(a => a.Id, ct);
        decimal td = 0, tc = 0;
        foreach (var kv in balances.Where(k => k.Key.counterpartyId != null && accs.ContainsKey(k.Key.accountId) && k.Value != 0)
                     .OrderBy(k => accs[k.Key.accountId].Number).ThenBy(k => names.GetValueOrDefault(k.Key.counterpartyId!.Value)))
        {
            var d = kv.Value > 0 ? kv.Value : 0;
            var c = kv.Value < 0 ? -kv.Value : 0;
            td += d; tc += c;
            r.Add(accs[kv.Key.accountId].Number, names.GetValueOrDefault(kv.Key.counterpartyId!.Value, "?"), d == 0 ? null : d, c == 0 ? null : c);
        }
        r.AddRow(new ReportRow("RAZEM", null, td, tc) { IsTotal = true });
        return r;
    }

    /// <summary>Bilans otwarcia (zapisy BO roku).</summary>
    public async Task<ReportDocument> OpeningBalanceAsync(Guid fiscalYearId, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var r = await NewAsync("Salda otwarcia (BO)", $"na dzień {fy.StartDate.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Konto"), new ReportColumn("Nazwa", ColumnKind.Text, 2.5), new ReportColumn("BO Wn", ColumnKind.Money), new ReportColumn("BO Ma", ColumnKind.Money) });
        var tb = await new LedgerQueries(_db).TrialBalanceAsync(fiscalYearId, fy.StartDate, fy.StartDate, ct);
        foreach (var t in tb.Where(t => t.IsLeaf && (t.OpeningDebit != 0 || t.OpeningCredit != 0)))
            r.Add(t.Account.Number, t.Account.Name, t.OpeningDebit, t.OpeningCredit);
        r.AddRow(new ReportRow("RAZEM", null, tb.Where(t => t.IsLeaf).Sum(t => t.OpeningDebit), tb.Where(t => t.IsLeaf).Sum(t => t.OpeningCredit)) { IsTotal = true });
        if (!fy.OpeningBalanceApproved) r.Notes.Add("Bilans otwarcia nie został zatwierdzony.");
        return r;
    }

    public async Task<ReportDocument> BalanceSheetAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var sb = new StatementBuilder(_db);
        var cur = await sb.BalanceSheetAsync(fiscalYearId, asOf, ct);
        var prevYear = await sb.PreviousYearAsync(fiscalYearId, ct);
        var prev = prevYear is null ? null : await sb.BalanceSheetAsync(prevYear.Id, prevYear.EndDate, ct);
        var r = await NewAsync("Bilans (załącznik nr 1 do ustawy o rachunkowości)", $"na dzień {asOf.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Poz."), new ReportColumn("Wyszczególnienie", ColumnKind.Text, 4), new ReportColumn($"Stan na {asOf.ToPl()}", ColumnKind.Money, 1.2),
            new ReportColumn(prevYear is null ? "Stan na koniec poprz. roku" : $"Stan na {prevYear.EndDate.ToPl()}", ColumnKind.Money, 1.2) });
        void Emit(IEnumerable<StatementLine> roots, string totalLabel, string totalKey)
        {
            foreach (var l in FinancialStatementLayout.Flatten(roots))
                r.AddRow(new ReportRow(Label(l.Code), l.Label, cur.Get(l.Code), prev?.Get(l.Code) ?? 0m) { Level = l.Depth, IsHeader = l.Children.Count > 0 });
            r.AddRow(new ReportRow(null, totalLabel, cur.Get(totalKey), prev?.Get(totalKey) ?? 0m) { IsTotal = true });
        }
        r.AddRow(new ReportRow(null, "AKTYWA", null, null) { IsHeader = true });
        Emit(FinancialStatementLayout.Assets, "AKTYWA RAZEM", "AKTYWA");
        r.AddRow(new ReportRow(null, "PASYWA", null, null) { IsHeader = true });
        Emit(FinancialStatementLayout.Liabilities, "PASYWA RAZEM", "PASYWA");
        r.Notes.AddRange(cur.Warnings);
        r.ControlsPassed = cur.Warnings.Count == 0;
        if (fy.Status != FiscalYearStatus.Zamkniety) r.Notes.Add("Rok obrotowy nie jest zamknięty — bilans ma charakter wstępny (wynik netto w poz. A.VI obejmuje bieżące obroty kont wynikowych).");
        r.Notes.Add("Układ wg załącznika nr 1 do UoR (jednostki inne). Dane porównawcze z ksiąg poprzedniego roku prowadzonych w programie.");
        return r;
    }

    public async Task<ReportDocument> ProfitAndLossAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await YearAsync(fiscalYearId, ct);
        var sb = new StatementBuilder(_db);
        var cur = await sb.ProfitAndLossAsync(fiscalYearId, asOf, ct);
        var prevYear = await sb.PreviousYearAsync(fiscalYearId, ct);
        var prev = prevYear is null ? null : await sb.ProfitAndLossAsync(prevYear.Id, prevYear.EndDate, ct);
        var r = await NewAsync("Rachunek zysków i strat (wariant porównawczy)", $"{fy.StartDate.ToPl()} – {asOf.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Poz."), new ReportColumn("Wyszczególnienie", ColumnKind.Text, 4), new ReportColumn("Rok bieżący", ColumnKind.Money, 1.2), new ReportColumn("Rok poprzedni", ColumnKind.Money, 1.2) });
        foreach (var l in FinancialStatementLayout.Flatten(FinancialStatementLayout.ProfitLoss))
            r.AddRow(new ReportRow(Label(l.Code), l.Label, cur.Get(l.Code), prev?.Get(l.Code) ?? 0m) { Level = l.Depth, IsHeader = l.Children.Count > 0, IsTotal = l.Formula is not null });
        r.Notes.AddRange(cur.Warnings);
        var bs = await sb.BalanceSheetAsync(fiscalYearId, asOf, ct);
        if (bs.Get("P.A.VI") != cur.Get("R.L"))
            r.Notes.Add($"Uwaga: wynik netto RZiS ({Money.Format(cur.Get("R.L"))}) ≠ pozycja bilansu A.VI ({Money.Format(bs.Get("P.A.VI"))}) — sprawdź zapisy na koncie wyniku finansowego.");
        r.ControlsPassed = cur.Warnings.Count == 0 && bs.Get("P.A.VI") == cur.Get("R.L");
        return r;
    }

    private static string Label(string code) => code.StartsWith("P.") ? code[2..] : code.StartsWith("R.") ? code[2..] : code;

    public async Task<ReportDocument> VatRegisterAsync(VatRegisterType register, DateOnly period, CancellationToken ct = default)
    {
        var p = new DateOnly(period.Year, period.Month, 1);
        var r = await NewAsync(register == VatRegisterType.Sprzedaz ? "Rejestr VAT sprzedaży" : "Rejestr VAT zakupów", $"okres rozliczeniowy {p:MM.yyyy}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Lp.", ColumnKind.Integer, 0.4), new ReportColumn("Dokument", ColumnKind.Text, 1.3), new ReportColumn("Data wyst.", ColumnKind.Date, 0.8),
            new ReportColumn(register == VatRegisterType.Sprzedaz ? "Data sprzed." : "Data wpływu", ColumnKind.Date, 0.8), new ReportColumn("Kontrahent", ColumnKind.Text, 2),
            new ReportColumn("NIP", ColumnKind.Text, 0.9), new ReportColumn("Stawka"), new ReportColumn("Netto", ColumnKind.Money), new ReportColumn("VAT", ColumnKind.Money),
            new ReportColumn(register == VatRegisterType.Zakup ? "VAT do odlicz." : "Pola JPK", register == VatRegisterType.Zakup ? ColumnKind.Money : ColumnKind.Text), new ReportColumn("KSeF", ColumnKind.Text, 1.2)
        });
        var rows = await new VatService(_db, _user, _clock).RegisterAsync(register, p, ct);
        foreach (var e in rows)
            r.Add(e.OrdinalNumber, e.DocumentNumber, e.IssueDate, register == VatRegisterType.Sprzedaz ? e.OperationDate : e.ReceiptDate, e.CounterpartyName, e.CounterpartyNip,
                e.VatRateCode, e.NetPln, e.VatPln, register == VatRegisterType.Zakup ? e.DeductibleVatPln : $"{e.JpkNetField}/{e.JpkTaxField}", e.KsefNumber ?? e.KsefMarker?.ToString());
        r.AddRow(new ReportRow(null, "RAZEM", null, null, null, null, null, rows.Sum(x => x.NetPln), rows.Sum(x => x.VatPln), register == VatRegisterType.Zakup ? rows.Sum(x => x.DeductibleVatPln) : null, null) { IsTotal = true });
        foreach (var g in rows.GroupBy(x => x.VatRateCode).OrderBy(g => g.Key))
            r.AddRow(new ReportRow(null, $"w tym stawka {g.Key}", null, null, null, null, null, g.Sum(x => x.NetPln), g.Sum(x => x.VatPln), register == VatRegisterType.Zakup ? g.Sum(x => x.DeductibleVatPln) : null, null) { IsTotal = true });
        return r;
    }

    public async Task<ReportDocument> AgingAsync(OpenItemSide side, DateOnly asOf, bool overdueOnly, CancellationToken ct = default)
    {
        var r = await NewAsync(side == OpenItemSide.Naleznosc ? (overdueOnly ? "Należności przeterminowane" : "Należności — wiekowanie") : (overdueOnly ? "Zobowiązania przeterminowane" : "Zobowiązania — wiekowanie"),
            $"na dzień {asOf.ToPl()}", true, ct);
        r.Filters.Add(("Tylko przeterminowane", overdueOnly ? "tak" : "nie"));
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Kontrahent", ColumnKind.Text, 2), new ReportColumn("Dokument", ColumnKind.Text, 1.3), new ReportColumn("Konto"), new ReportColumn("Data dok.", ColumnKind.Date),
            new ReportColumn("Termin", ColumnKind.Date), new ReportColumn("Waluta"), new ReportColumn("Kwota", ColumnKind.Money), new ReportColumn("Pozostało", ColumnKind.Money),
            new ReportColumn("Pozostało PLN", ColumnKind.Money), new ReportColumn("Dni po terminie", ColumnKind.Integer), new ReportColumn("Przedział")
        });
        var rows = await new SettlementService(_db, _user, _clock).AgingAsync(side, asOf, overdueOnly, null, ct);
        foreach (var x in rows) r.Add(x.CounterpartyName, x.Reference, x.Account, x.DocumentDate, x.DueDate, x.Currency, x.Amount, x.Remaining, x.RemainingPln, x.DaysOverdue, x.Bucket);
        r.AddRow(new ReportRow("RAZEM", null, null, null, null, null, null, null, rows.Sum(x => x.RemainingPln), null, null) { IsTotal = true });
        foreach (var b in SettlementService.Buckets)
            r.AddRow(new ReportRow($"w tym: {b.name}", null, null, null, null, null, null, null, rows.Where(x => x.Bucket == b.name).Sum(x => x.RemainingPln), null, null) { IsTotal = true });
        return r;
    }

    public async Task<ReportDocument> FixedAssetsAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var r = await NewAsync("Ewidencja środków trwałych i WNiP", $"stan na {asOf.ToPl()}", true, ct);
        r.Columns.AddRange(new[]
        {
            new ReportColumn("Nr inw."), new ReportColumn("Nazwa", ColumnKind.Text, 2), new ReportColumn("KŚT"), new ReportColumn("Przyjęcie", ColumnKind.Date),
            new ReportColumn("Wartość pocz.", ColumnKind.Money), new ReportColumn("Metoda/stawka bil."), new ReportColumn("Umorzenie bil.", ColumnKind.Money),
            new ReportColumn("Netto bil.", ColumnKind.Money), new ReportColumn("Metoda/stawka pod."), new ReportColumn("Umorzenie pod.", ColumnKind.Money), new ReportColumn("Status")
        });
        var svc = new FixedAssetService(_db, _user, _clock);
        var assets = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId && a.PlacedInServiceDate <= asOf).OrderBy(a => a.InventoryNumber).ToListAsync(ct);
        decimal tg = 0, tab = 0, tn = 0, tat = 0;
        foreach (var a in assets)
        {
            var (gross, accB, accT, net) = await svc.ValuesAsOfAsync(a.Id, asOf, ct);
            if (a.Status is FixedAssetStatus.Zlikwidowany or FixedAssetStatus.Sprzedany && a.DisposalDate <= asOf) { gross = 0; accB = 0; net = 0; accT = 0; }
            tg += gross; tab += accB; tn += net; tat += accT;
            r.Add(a.InventoryNumber, a.Name, a.KstSymbol, a.PlacedInServiceDate, gross, $"{a.BookMethod} {a.BookRatePercent:0.##}%", accB, net, $"{a.TaxMethod} {a.TaxRatePercent:0.##}%", accT, a.Status.ToString());
        }
        r.AddRow(new ReportRow("RAZEM", null, null, null, tg, null, tab, tn, null, tat, null) { IsTotal = true });
        return r;
    }

    public async Task<ReportDocument> DepreciationScheduleAsync(int year, DepreciationBasis basis, CancellationToken ct = default)
    {
        var r = await NewAsync($"Zestawienie amortyzacji {(basis == DepreciationBasis.Bilansowa ? "bilansowej" : "podatkowej")}", $"rok {year}", true, ct);
        r.Columns.Add(new ReportColumn("Nr inw."));
        r.Columns.Add(new ReportColumn("Nazwa", ColumnKind.Text, 1.8));
        for (var m = 1; m <= 12; m++) r.Columns.Add(new ReportColumn(m.ToString("00"), ColumnKind.Money, 0.7));
        r.Columns.Add(new ReportColumn("Razem", ColumnKind.Money));
        var assets = await _db.FixedAssets.AsNoTracking().Include(a => a.Operations).Where(a => a.CompanyId == _user.CompanyId).OrderBy(a => a.InventoryNumber).ToListAsync(ct);
        var charges = await _db.DepreciationCharges.AsNoTracking().Where(c => c.CompanyId == _user.CompanyId && c.Year == year).ToListAsync(ct);
        var totals = new decimal[13];
        foreach (var a in assets)
        {
            var plan = DepreciationCalculator.Plan(a, basis).Where(p => p.Year == year).ToDictionary(p => p.Month, p => p.Amount);
            var cells = new List<object?> { a.InventoryNumber, a.Name };
            decimal sum = 0;
            for (var m = 1; m <= 12; m++)
            {
                var c = charges.FirstOrDefault(x => x.FixedAssetId == a.Id && x.Month == m);
                var v = c is not null ? (basis == DepreciationBasis.Bilansowa ? c.BookAmount : c.TaxAmount) : plan.GetValueOrDefault(m);
                cells.Add(v == 0 ? null : v);
                sum += v;
                totals[m - 1] += v;
            }
            totals[12] += sum;
            cells.Add(sum);
            if (sum != 0) r.Add(cells.ToArray());
        }
        var tr = new List<object?> { "RAZEM", null };
        tr.AddRange(totals.Select(t => (object?)t));
        r.AddRow(new ReportRow(tr.ToArray()) { IsTotal = true });
        r.Notes.Add("Miesiące z naliczeniem — kwoty zaksięgowane; pozostałe — wg planu amortyzacji.");
        return r;
    }

    public async Task<ReportDocument> BankReconciliationAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var r = await NewAsync("Uzgodnienie sald rachunków bankowych i kasy", $"na dzień {asOf.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Rachunek", ColumnKind.Text, 1.6), new ReportColumn("Konto"), new ReportColumn("Saldo ksiąg", ColumnKind.Money),
            new ReportColumn("Saldo wyciągu", ColumnKind.Money), new ReportColumn("Data wyciągu", ColumnKind.Date), new ReportColumn("Różnica", ColumnKind.Money),
            new ReportColumn("Niezaksięgowane (szt.)", ColumnKind.Integer), new ReportColumn("Niezaksięgowane (kwota)", ColumnKind.Money) });
        var rows = await new BankReconciliation(_db).ReconcileAsync(_user.CompanyId, fiscalYearId, asOf, ct);
        foreach (var x in rows) r.Add(x.AccountName, x.LedgerAccount, x.LedgerBalance, x.StatementBalance, x.StatementDate, x.Difference, x.UnpostedTransactions, x.UnpostedAmount);
        r.ControlsPassed = rows.All(x => x.Difference is null or 0);
        if (rows.Any(x => x.Difference is null)) r.Notes.Add("Dla części rachunków brak wyciągu z saldem na dzień uzgodnienia.");
        return r;
    }

    public async Task<ReportDocument> ChangeLogAsync(DateTime fromUtc, DateTime toUtc, string? entityType = null, CancellationToken ct = default)
    {
        _user.Require(Permission.ViewAuditLog);
        var r = await NewAsync("Raport zmian (dziennik audytowy)", $"{fromUtc.ToPlDateTime()} – {toUtc.ToPlDateTime()}", true, ct);
        if (entityType is not null) r.Filters.Add(("Obiekt", entityType));
        r.Columns.AddRange(new[] { new ReportColumn("Nr", ColumnKind.Integer, 0.5), new ReportColumn("Czas", ColumnKind.Text, 1.2), new ReportColumn("Użytkownik"),
            new ReportColumn("Operacja", ColumnKind.Text, 1.5), new ReportColumn("Obiekt"), new ReportColumn("Szczegóły", ColumnKind.Text, 4) });
        var q = _db.AuditEvents.AsNoTracking().Where(a => a.AtUtc >= fromUtc && a.AtUtc <= toUtc);
        if (entityType is not null) q = q.Where(a => a.EntityType == entityType);
        foreach (var a in await q.OrderBy(a => a.Sequence).Take(50000).ToListAsync(ct))
            r.Add(a.Sequence, a.AtUtc.ToPlDateTime(), a.UserLogin, a.Action, a.EntityType, a.Details);
        return r;
    }

    public async Task<ReportDocument> PeriodCloseReportAsync(Guid periodId, CancellationToken ct = default)
    {
        var period = await _db.Periods.AsNoTracking().FirstAsync(p => p.Id == periodId, ct);
        var r = await NewAsync($"Raport zamknięcia okresu {period.Name}", $"{period.StartDate.ToPl()} – {period.EndDate.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Kontrola", ColumnKind.Text, 2), new ReportColumn("Wynik"), new ReportColumn("Waga"), new ReportColumn("Szczegóły", ColumnKind.Text, 3) });
        var checklist = await new PeriodService(_db, _user, _clock).BuildChecklistAsync(periodId, ct);
        foreach (var i in checklist.Items) r.Add(i.Title, i.Passed ? "OK" : "NIE", i.Severity.ToString(), i.Details);
        r.AddRow(new ReportRow("Status okresu", period.Status.ToString(), null, period.ClosedAtUtc?.ToPlDateTime()) { IsTotal = true });
        var history = await _db.PeriodStatusChanges.AsNoTracking().Where(h => h.PeriodId == periodId).OrderBy(h => h.AtUtc).ToListAsync(ct);
        foreach (var h in history) r.Notes.Add($"{h.AtUtc.ToPlDateTime()}: {h.FromStatus} → {h.ToStatus}; {h.Reason}");
        r.ControlsPassed = !checklist.HasBlocking;
        return r;
    }

    public async Task<ReportDocument> IntegrityReportAsync(CancellationToken ct = default)
    {
        var r = await NewAsync("Raport kontroli integralności ksiąg", "wszystkie lata obrotowe", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Obszar", ColumnKind.Text, 2), new ReportColumn("Wynik"), new ReportColumn("Szczegóły", ColumnKind.Text, 4) });
        var rep = await new IntegrityService(_db, _clock).FullCheckAsync(_user.CompanyId, ct);
        foreach (var (area, ok, details) in rep.Results) r.Add(area, ok ? "OK" : "BŁĄD", details);
        r.ControlsPassed = rep.AllOk;
        return r;
    }

    public async Task<ReportDocument> CitReconciliationAsync(Guid fiscalYearId, int uptoPeriod, CancellationToken ct = default)
    {
        var rec = await new CitService(_db, _user, _clock).ReconcileAsync(fiscalYearId, uptoPeriod, ct);
        var r = await NewAsync("Uzgodnienie wyniku bilansowego z podatkowym (CIT)", $"{rec.From.ToPl()} – {rec.To.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Pozycja", ColumnKind.Text, 3), new ReportColumn("Kwota", ColumnKind.Money), new ReportColumn("Podstawa", ColumnKind.Text, 2) });
        r.Add("Przychody bilansowe", rec.BookRevenue, null);
        r.Add("Koszty bilansowe (bez podatku dochodowego)", rec.BookCosts - rec.Adjustments.Where(a => a.Item.StartsWith("Podatek dochodowy")).Sum(a => a.Amount), null);
        r.AddRow(new ReportRow("Wynik bilansowy brutto", rec.BookResultBeforeTax, null) { IsTotal = true });
        foreach (var a in rec.Adjustments) r.Add(a.Item, a.Amount, a.Basis);
        r.AddRow(new ReportRow("Przychody podatkowe", rec.TaxRevenue, null) { IsTotal = true });
        r.AddRow(new ReportRow("Koszty uzyskania przychodów", rec.TaxCosts, null) { IsTotal = true });
        r.AddRow(new ReportRow("Dochód / (strata)", rec.Computation.Income - rec.Computation.Loss, null) { IsTotal = true });
        r.Add("Odliczenia od dochodu", rec.Deductions, null);
        r.AddRow(new ReportRow("Podstawa opodatkowania (zaokrąglona)", rec.Computation.TaxBase, "art. 63 Ordynacji podatkowej") { IsTotal = true });
        r.AddRow(new ReportRow($"Podatek {rec.Computation.RatePercent:0}%", rec.Computation.Tax, "art. 19 ustawy o CIT") { IsTotal = true });
        foreach (var (acc, name, amt) in rec.ToAnalyze) r.Notes.Add($"Do analizy: {acc} {name} — {Money.Format(amt)}");
        r.Notes.AddRange(rec.Warnings);
        r.Notes.Add("Zestawienie pomocnicze — nie stanowi zeznania CIT-8 ani deklaracji podatkowej.");
        return r;
    }

    /// <summary>Opis systemu informatycznego (art. 10 ust. 1 pkt 3 lit. c UoR) — generowany z bieżącej konfiguracji.</summary>
    public async Task<ReportDocument> SystemDescriptionAsync(string databaseDescription, CancellationToken ct = default)
    {
        var r = await NewAsync("Opis systemu informatycznego (art. 10 ust. 1 pkt 3 lit. c UoR)", $"stan na {_clock.Today.ToPl()}", false, ct);
        r.Columns.AddRange(new[] { new ReportColumn("Element", ColumnKind.Text, 1.5), new ReportColumn("Opis", ColumnKind.Text, 4) });
        var asm = typeof(ReportService).Assembly.GetName();
        var migrations = await _db.Database.GetAppliedMigrationsAsync(ct);
        r.Add("Program", $"Pełna Księgowość PSA {asm.Version}");
        r.Add("Wersja schematu bazy", migrations.LastOrDefault() ?? "brak");
        r.Add("Baza danych", databaseDescription);
        r.Add("Zbiory tworzące księgi", "Dziennik (tabela JournalEntries/JournalLines), księga główna i księgi pomocnicze (konta, rozrachunki OpenItems, ewidencja ŚT), zestawienia obrotów i sald (raport), rejestry VAT (VatRegister), dokumenty i załączniki (Documents, Attachments).");
        r.Add("Zapis trwały", "Zapisy zaksięgowane są niemodyfikowalne (logika aplikacji + wyzwalacze bazy); korekta wyłącznie storno i zapisem korygującym. Numeracja ciągła w roku; łańcuch skrótów SHA-256 dziennika.");
        r.Add("Identyfikacja osób", "Każdy zapis zawiera identyfikator użytkownika wprowadzającego i zatwierdzającego; dziennik audytowy z łańcuchem skrótów.");
        r.Add("Algorytmy", "Zaokrąglenia: do grosza, połówki od zera; VAT od sumy netto w stawce; podatki do pełnych zł (art. 63 OP); amortyzacja od miesiąca następnego po przyjęciu; RZiS wariant porównawczy; salda rozrachunków bez kompensat.");
        r.Add("Ochrona danych", "Konta użytkowników z rolami, hasła PBKDF2-SHA256, opcjonalne MFA (TOTP), blokada po nieudanych logowaniach, szyfrowane kopie zapasowe AES-256-GCM z manifestem SHA-256, sekrety w Windows Credential Manager.");
        r.Add("Kopie zapasowe", "Ręczne i automatyczne (harmonogram), lokalne i opcjonalnie OVHcloud Object Storage; weryfikacja integralności; odtwarzanie do osobnego katalogu z kontrolą kompletności.");
        r.Notes.Add("Dokument stanowi załącznik do polityki rachunkowości — wymaga zatwierdzenia przez kierownika jednostki wraz z datą rozpoczęcia eksploatacji.");
        return r;
    }
}
