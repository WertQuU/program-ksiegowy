using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Ledger;

namespace PelnaKsiegowosc.Application.Ledger;

public sealed record LedgerLine(
    Guid EntryId, long Sequence, string JournalNumber, string JournalCode, EntryKind Kind, DateOnly PostingDate, DateOnly OperationDate,
    string DocumentNumber, string Description, Guid AccountId, string AccountNumber, decimal Debit, decimal Credit, Guid? CounterpartyId, Guid PeriodId);

public sealed class AccountTotals
{
    public required Account Account { get; init; }
    public decimal OpeningDebit { get; set; }
    public decimal OpeningCredit { get; set; }
    public decimal PeriodDebit { get; set; }
    public decimal PeriodCredit { get; set; }
    public decimal CumulativeDebit { get; set; }
    public decimal CumulativeCredit { get; set; }
    public bool IsLeaf { get; set; }

    public decimal TotalDebit => OpeningDebit + CumulativeDebit;
    public decimal TotalCredit => OpeningCredit + CumulativeCredit;
    public decimal Balance => TotalDebit - TotalCredit;
    public decimal BalanceDebit => Balance > 0 ? Balance : 0;
    public decimal BalanceCredit => Balance < 0 ? -Balance : 0;
    public bool HasActivity => OpeningDebit != 0 || OpeningCredit != 0 || CumulativeDebit != 0 || CumulativeCredit != 0;
}

/// <summary>
/// Odczyty z ksiąg (tylko zapisy zaksięgowane). Agregacje wykonywane są w pamięci na projekcjach wierszy —
/// SQLite nie obsługuje natywnie arytmetyki na typie decimal, a wynik musi być identyczny dla obu silników.
/// </summary>
public sealed class LedgerQueries
{
    private readonly KsiegiDbContext _db;

    public LedgerQueries(KsiegiDbContext db) => _db = db;

    public async Task<List<LedgerLine>> LinesAsync(Guid fiscalYearId, DateOnly? from = null, DateOnly? to = null, bool includeOpening = true,
        IReadOnlyCollection<Guid>? accountIds = null, CancellationToken ct = default)
    {
        var q = from l in _db.JournalLines.AsNoTracking()
                join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                where e.FiscalYearId == fiscalYearId && e.Status == EntryStatus.Zaksiegowany
                select new { l, e };
        if (from is { } f) q = q.Where(x => x.e.PostingDate >= f);
        if (to is { } t) q = q.Where(x => x.e.PostingDate <= t);
        if (!includeOpening) q = q.Where(x => x.e.Kind != EntryKind.BilansOtwarcia);
        if (accountIds is { Count: > 0 }) q = q.Where(x => accountIds.Contains(x.l.AccountId));
        var rows = await q.Select(x => new LedgerLine(x.e.Id, x.e.SequenceNumber ?? 0, x.e.JournalNumber ?? "", x.e.JournalCode, x.e.Kind, x.e.PostingDate, x.e.OperationDate,
            x.e.DocumentNumber, x.e.Description, x.l.AccountId, x.l.AccountNumber, x.l.Debit, x.l.Credit, x.l.CounterpartyId, x.e.PeriodId)).ToListAsync(ct);
        return rows.OrderBy(r => r.Sequence).ToList();
    }

    /// <summary>
    /// Obroty i salda kont za okres [periodFrom; periodTo] roku obrotowego. BO = zapisy rodzaju „bilans otwarcia”.
    /// Konta syntetyczne zawierają sumę kont analitycznych.
    /// </summary>
    public async Task<List<AccountTotals>> TrialBalanceAsync(Guid fiscalYearId, DateOnly periodFrom, DateOnly periodTo, CancellationToken ct = default)
    {
        var chart = await _db.Charts.AsNoTracking().Where(c => c.FiscalYearId == fiscalYearId).OrderByDescending(c => c.Version).FirstAsync(ct);
        var accounts = await _db.Accounts.AsNoTracking().Where(a => a.ChartId == chart.Id).ToListAsync(ct);
        var parents = accounts.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).ToHashSet();
        var totals = accounts.ToDictionary(a => a.Id, a => new AccountTotals { Account = a, IsLeaf = !parents.Contains(a.Id) });
        var lines = await LinesAsync(fiscalYearId, null, periodTo, true, null, ct);
        foreach (var l in lines)
        {
            if (!totals.TryGetValue(l.AccountId, out var t)) continue;
            if (l.Kind == EntryKind.BilansOtwarcia)
            {
                t.OpeningDebit += l.Debit;
                t.OpeningCredit += l.Credit;
                continue;
            }
            t.CumulativeDebit += l.Debit;
            t.CumulativeCredit += l.Credit;
            if (l.PostingDate >= periodFrom)
            {
                t.PeriodDebit += l.Debit;
                t.PeriodCredit += l.Credit;
            }
        }
        // agregacja w górę hierarchii
        var byId = accounts.ToDictionary(a => a.Id);
        foreach (var leaf in totals.Values.Where(t => t.IsLeaf).ToList())
        {
            var p = leaf.Account.ParentId;
            while (p is { } pid && totals.TryGetValue(pid, out var parent))
            {
                parent.OpeningDebit += leaf.OpeningDebit;
                parent.OpeningCredit += leaf.OpeningCredit;
                parent.PeriodDebit += leaf.PeriodDebit;
                parent.PeriodCredit += leaf.PeriodCredit;
                parent.CumulativeDebit += leaf.CumulativeDebit;
                parent.CumulativeCredit += leaf.CumulativeCredit;
                p = byId[pid].ParentId;
            }
        }
        return totals.Values.OrderBy(t => t.Account.Number, StringComparer.Ordinal).ToList();
    }

    /// <summary>Saldo konta/kont (liście) per kontrahent na dzień — podstawa prezentacji rozrachunków bez kompensat.</summary>
    public async Task<Dictionary<(Guid accountId, Guid? counterpartyId), decimal>> BalancesByCounterpartyAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var lines = await LinesAsync(fiscalYearId, null, asOf, true, null, ct);
        return lines.GroupBy(l => (l.AccountId, l.CounterpartyId)).ToDictionary(g => g.Key, g => g.Sum(x => x.Debit - x.Credit));
    }
}
