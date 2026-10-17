using Microsoft.EntityFrameworkCore;
using PsaKsiegi.Application.Audit;
using PsaKsiegi.Application.Ledger;
using PsaKsiegi.Data;
using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Ledger;
using PsaKsiegi.Domain.Periods;

namespace PsaKsiegi.Application.Integrity;

public sealed record IntegrityReport(DateTime CheckedAtUtc, IReadOnlyList<(string Area, bool Ok, string Details)> Results)
{
    public bool AllOk => Results.All(r => r.Ok);
}

/// <summary>
/// Kontrola integralności ksiąg: ciągłość numeracji dziennika, łańcuch skrótów SHA-256, bilansowanie zapisów,
/// zgodność obrotów dziennika z ZOiS, zgodność rozrachunków z saldami kont, ciągłość BO = BZ, łańcuch dziennika audytowego.
/// </summary>
public sealed class IntegrityService
{
    private readonly KsiegiDbContext _db;
    private readonly IClock _clock;

    public IntegrityService(KsiegiDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<List<string>> CheckLedgerChainAsync(Guid fiscalYearId, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var entries = await _db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.FiscalYearId == fiscalYearId && e.Status == EntryStatus.Zaksiegowany).ToListAsync(ct);
        string? prev = null;
        long expected = 1;
        foreach (var e in entries.OrderBy(e => e.SequenceNumber))
        {
            if (e.SequenceNumber != expected) problems.Add($"Luka w numeracji dziennika: oczekiwano pozycji {expected}, jest {e.SequenceNumber}.");
            if (e.PreviousHash != prev) problems.Add($"Pozycja {e.SequenceNumber}: przerwany łańcuch skrótów.");
            if (e.ComputeHash() != e.Hash) problems.Add($"Pozycja {e.SequenceNumber} ({e.JournalNumber}): treść zapisu niezgodna ze skrótem — możliwa nieautoryzowana zmiana.");
            if (e.TotalDebit != e.TotalCredit && e.Lines.Count > 0)
                problems.Add($"Pozycja {e.SequenceNumber}: zapis nie bilansuje się (Wn {Money.Format(e.TotalDebit)} / Ma {Money.Format(e.TotalCredit)}).");
            prev = e.Hash;
            expected = (e.SequenceNumber ?? expected) + 1;
        }
        return problems;
    }

    /// <summary>
    /// Rozrachunki: (1) każda pozycja na koncie rozrachunkowym wskazuje pozycję rozrachunkową; (2) saldo konta per kontrahent
    /// = suma sald pozycji rozrachunkowych na dzień (z zapisów roku, łącznie z BO); (3) stan zapamiętany w pozycjach
    /// rozrachunkowych (kwota, rozliczono) jest zgodny z sumą zapisów ze wszystkich lat.
    /// </summary>
    public async Task<List<string>> CheckOpenItemsAgainstLedgerAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.AsNoTracking().FirstAsync(f => f.Id == fiscalYearId, ct);
        var problems = new List<string>();
        var balances = await OpenItemBalancesAsync(fiscalYearId, asOf, ct);
        foreach (var missing in balances.UnlinkedLines)
            problems.Add($"Zapis {missing} na koncie rozrachunkowym nie wskazuje pozycji rozrachunkowej.");

        // stan zapamiętany w pozycjach vs suma wszystkich zapisów (bez BO)
        var items = await _db.OpenItems.AsNoTracking().Where(o => o.CompanyId == fy.CompanyId).ToDictionaryAsync(o => o.Id, ct);
        var allLines = await (from l in _db.JournalLines.AsNoTracking()
                              join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                              where e.CompanyId == fy.CompanyId && e.Status == EntryStatus.Zaksiegowany && l.OpenItemId != null && e.Kind != EntryKind.BilansOtwarcia
                              select new { l.OpenItemId, l.Debit, l.Credit }).ToListAsync(ct);
        foreach (var g in allLines.GroupBy(x => x.OpenItemId!.Value))
        {
            if (!items.TryGetValue(g.Key, out var item)) { problems.Add($"Zapis wskazuje nieistniejącą pozycję rozrachunkową {g.Key}."); continue; }
            var signed = g.Sum(x => x.Debit - x.Credit);
            var expected = item.Side == Domain.Settlements.OpenItemSide.Naleznosc ? item.RemainingPln : -item.RemainingPln;
            if (signed != expected) problems.Add($"Rozrachunek {item.Reference}: pozostało wg pozycji {Money.Format(item.RemainingPln)}, wg zapisów {Money.Format(Math.Abs(signed))}.");
        }
        return problems;
    }

    public sealed record OpenItemBalances(Dictionary<Guid, decimal> ByItem, Dictionary<(string account, Guid? cp), decimal> ByAccountAndCounterparty, List<string> UnlinkedLines);

    /// <summary>Saldo (Wn − Ma) każdej pozycji rozrachunkowej w danym roku na dzień, łącznie z BO.</summary>
    public async Task<OpenItemBalances> OpenItemBalancesAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var chart = await _db.Charts.AsNoTracking().Where(c => c.FiscalYearId == fiscalYearId).OrderByDescending(c => c.Version).FirstAsync(ct);
        var ids = await _db.Accounts.AsNoTracking().Where(a => a.ChartId == chart.Id && a.IsSettlementAccount).Select(a => a.Id).ToListAsync(ct);
        var byItem = new Dictionary<Guid, decimal>();
        var byAcc = new Dictionary<(string, Guid?), decimal>();
        var unlinked = new List<string>();
        if (ids.Count == 0) return new(byItem, byAcc, unlinked);
        var rows = await (from l in _db.JournalLines.AsNoTracking()
                          join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                          where e.FiscalYearId == fiscalYearId && e.Status == EntryStatus.Zaksiegowany && ids.Contains(l.AccountId) && e.PostingDate <= asOf
                          select new { l.OpenItemId, l.AccountNumber, l.CounterpartyId, l.Debit, l.Credit, e.JournalNumber, l.LineNo }).ToListAsync(ct);
        foreach (var r in rows)
        {
            var v = r.Debit - r.Credit;
            if (r.OpenItemId is { } oi) byItem[oi] = byItem.GetValueOrDefault(oi) + v;
            else unlinked.Add($"{r.JournalNumber} poz. {r.LineNo}");
            var k = (r.AccountNumber, r.CounterpartyId);
            byAcc[k] = byAcc.GetValueOrDefault(k) + v;
        }
        return new(byItem, byAcc, unlinked);
    }

    public async Task<IntegrityReport> FullCheckAsync(Guid companyId, CancellationToken ct = default)
    {
        var results = new List<(string, bool, string)>();
        var years = await _db.FiscalYears.AsNoTracking().Where(f => f.CompanyId == companyId).OrderBy(f => f.StartDate).ToListAsync(ct);
        var queries = new LedgerQueries(_db);
        FiscalYear? prev = null;
        foreach (var fy in years)
        {
            var chain = await CheckLedgerChainAsync(fy.Id, ct);
            results.Add(($"Dziennik {fy.Name}: numeracja i skróty", chain.Count == 0, chain.Count == 0 ? "OK" : string.Join(" | ", chain.Take(10))));

            if (await _db.Charts.AnyAsync(c => c.FiscalYearId == fy.Id, ct))
            {
                var tb = await queries.TrialBalanceAsync(fy.Id, fy.StartDate, fy.EndDate, ct);
                var leaves = tb.Where(t => t.IsLeaf && t.Account.Type != Domain.Accounts.AccountType.Pozabilansowe).ToList();
                var lines = await queries.LinesAsync(fy.Id, null, null, true, null, ct);
                var journalDebit = lines.Sum(l => l.Debit);
                var tbDebit = leaves.Sum(t => t.OpeningDebit + t.CumulativeDebit);
                var tbCredit = leaves.Sum(t => t.OpeningCredit + t.CumulativeCredit);
                var offBalance = tb.Where(t => t.IsLeaf && t.Account.Type == Domain.Accounts.AccountType.Pozabilansowe).Sum(t => t.OpeningDebit + t.CumulativeDebit);
                var ok = tbDebit == tbCredit && journalDebit == tbDebit + offBalance;
                results.Add(($"ZOiS {fy.Name}: zgodność z dziennikiem", ok,
                    $"Obroty dziennika Wn {Money.Format(journalDebit)}; ZOiS Wn {Money.Format(tbDebit)} / Ma {Money.Format(tbCredit)}"));

                var oi = await CheckOpenItemsAgainstLedgerAsync(fy.Id, fy.EndDate, ct);
                if (fy.Status == FiscalYearStatus.Otwarty)
                    results.Add(($"Rozrachunki {fy.Name}: zgodność z saldami", oi.Count == 0, oi.Count == 0 ? "OK" : string.Join(" | ", oi.Take(10))));

                if (prev is not null && prev.Status == FiscalYearStatus.Zamkniety)
                {
                    var bz = await queries.TrialBalanceAsync(prev.Id, prev.StartDate, prev.EndDate, ct);
                    var bzTotal = bz.Where(t => t.IsLeaf && t.Account.IsBalanceAccount).Sum(t => t.BalanceDebit);
                    var boTotal = tb.Where(t => t.IsLeaf).Sum(t => t.OpeningDebit);
                    var resultDebit = bz.Where(t => t.IsLeaf && t.Account.Type == Domain.Accounts.AccountType.WynikFinansowy).Sum(t => t.BalanceDebit);
                    var okBo = bzTotal + resultDebit == boTotal;
                    results.Add(($"Ciągłość BO {fy.Name} = BZ {prev.Name}", okBo, $"BZ (Wn) {Money.Format(bzTotal + resultDebit)}; BO (Wn) {Money.Format(boTotal)}"));
                }
            }
            prev = fy;
        }
        var audit = await new AuditService(_db, _clock).VerifyChainAsync(ct);
        results.Add(("Dziennik audytowy", audit.Count == 0, audit.Count == 0 ? "OK" : string.Join(" | ", audit.Take(10))));

        var attachments = await _db.Attachments.AsNoTracking().CountAsync(ct);
        results.Add(("Załączniki (liczba w bazie)", true, attachments.ToString()));
        return new IntegrityReport(_clock.UtcNow, results);
    }
}
