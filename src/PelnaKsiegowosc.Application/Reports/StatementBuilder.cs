using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Reporting;

namespace PelnaKsiegowosc.Application.Reports;

public sealed class StatementValues
{
    public required DateOnly AsOf { get; init; }
    public Dictionary<string, decimal> Lines { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>Konta z saldem bez przypisania do pozycji sprawozdania.</summary>
    public List<(string Account, decimal Balance)> Unmapped { get; } = new();
    public decimal Get(string code) => Lines.GetValueOrDefault(code);
}

/// <summary>
/// Wyliczenie bilansu i rachunku zysków i strat (wariant porównawczy) z ksiąg. Salda kont rozrachunkowych
/// prezentowane są per kontrahent bez kompensat (art. 7 ust. 3 UoR): saldo Wn → aktywa, saldo Ma → pasywa.
/// </summary>
public sealed class StatementBuilder
{
    /// <summary>Pary pozycji prezentowanych per saldo (zysk/strata na rozchodzie).</summary>
    private static readonly (string gain, string loss)[] NettingPairs = { ("R.D.I", "R.E.I"), ("R.G.III", "R.H.II") };

    private readonly KsiegiDbContext _db;

    public StatementBuilder(KsiegiDbContext db) => _db = db;

    private async Task<(List<Account> accounts, HashSet<Guid> parents)> ChartAsync(Guid fiscalYearId, CancellationToken ct)
    {
        var chart = await _db.Charts.AsNoTracking().Where(c => c.FiscalYearId == fiscalYearId).OrderByDescending(c => c.Version).FirstAsync(ct);
        var accounts = await _db.Accounts.AsNoTracking().Where(a => a.ChartId == chart.Id).ToListAsync(ct);
        return (accounts, accounts.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).ToHashSet());
    }

    private async Task<List<(Guid AccountId, Guid? Cp, decimal Debit, decimal Credit, EntryKind Kind)>> LinesAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct) =>
        (await (from l in _db.JournalLines.AsNoTracking()
                join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                where e.FiscalYearId == fiscalYearId && e.Status == EntryStatus.Zaksiegowany && e.PostingDate <= asOf
                select new { l.AccountId, l.CounterpartyId, l.Debit, l.Credit, e.Kind }).ToListAsync(ct))
        .Select(x => (x.AccountId, x.CounterpartyId, x.Debit, x.Credit, x.Kind)).ToList();

    public async Task<StatementValues> BalanceSheetAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var (accounts, parents) = await ChartAsync(fiscalYearId, ct);
        var byId = accounts.ToDictionary(a => a.Id);
        var lines = await LinesAsync(fiscalYearId, asOf, ct);
        var v = new StatementValues { AsOf = asOf };

        void AddTo(string code, decimal amount) => v.Lines[code] = v.Lines.GetValueOrDefault(code) + amount;
        static bool IsLiability(string code) => code.StartsWith("P.");

        foreach (var g in lines.GroupBy(l => (l.AccountId, l.Cp)))
        {
            if (!byId.TryGetValue(g.Key.AccountId, out var a)) continue;
            var balance = g.Sum(x => x.Debit - x.Credit);
            if (balance == 0) continue;
            switch (a.Type)
            {
                case AccountType.Pozabilansowe:
                    break;
                case AccountType.WynikoweKosztowe:
                case AccountType.WynikowePrzychodowe:
                case AccountType.WynikFinansowy:
                    AddTo("P.A.VI", -balance);
                    break;
                case AccountType.AktywnoPasywne:
                    if (balance > 0 && a.BalanceDebitLine is { } dl) AddTo(dl, IsLiability(dl) ? -balance : balance);
                    else if (balance < 0 && a.BalanceCreditLine is { } cl) AddTo(cl, IsLiability(cl) ? -balance : balance);
                    else v.Unmapped.Add((a.Number, balance));
                    break;
                default:
                    var line = a.BalanceDebitLine ?? a.BalanceCreditLine;
                    if (line is null) { v.Unmapped.Add((a.Number, balance)); break; }
                    AddTo(line, IsLiability(line) ? -balance : balance);
                    break;
            }
        }
        foreach (var u in v.Unmapped.GroupBy(u => u.Account))
            v.Warnings.Add($"Konto {u.Key} (saldo {Domain.Common.Money.Format(u.Sum(x => x.Balance))}) nie jest przypisane do pozycji bilansu.");
        Aggregate(v, FinancialStatementLayout.Assets);
        Aggregate(v, FinancialStatementLayout.Liabilities);
        v.Lines["AKTYWA"] = v.Get("A") + v.Get("B") + v.Get("C") + v.Get("D");
        v.Lines["PASYWA"] = v.Get("P.A") + v.Get("P.B");
        if (v.Get("AKTYWA") != v.Get("PASYWA"))
            v.Warnings.Add($"Suma aktywów ({Domain.Common.Money.Format(v.Get("AKTYWA"))}) ≠ suma pasywów ({Domain.Common.Money.Format(v.Get("PASYWA"))}).");
        return v;
    }

    /// <summary>RZiS — obroty kont wynikowych w roku do dnia (z pominięciem zapisów zamknięcia roku).</summary>
    public async Task<StatementValues> ProfitAndLossAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var (accounts, _) = await ChartAsync(fiscalYearId, ct);
        var byId = accounts.ToDictionary(a => a.Id);
        var lines = await LinesAsync(fiscalYearId, asOf, ct);
        var v = new StatementValues { AsOf = asOf };
        foreach (var g in lines.Where(l => l.Kind != EntryKind.Zamkniecie && l.Kind != EntryKind.BilansOtwarcia).GroupBy(l => l.AccountId))
        {
            if (!byId.TryGetValue(g.Key, out var a) || !a.IsResultAccount) continue;
            var net = g.Sum(x => x.Debit - x.Credit);
            if (net == 0) continue;
            if (a.ProfitLossLine is not { } code)
            {
                v.Unmapped.Add((a.Number, net));
                v.Warnings.Add($"Konto {a.Number} nie jest przypisane do pozycji RZiS (obroty {Domain.Common.Money.Format(net)}).");
                continue;
            }
            var section = code.Split('.')[1];
            var isRevenueSection = section is "A" or "D" or "G";
            v.Lines[code] = v.Lines.GetValueOrDefault(code) + (isRevenueSection ? -net : net);
        }
        foreach (var (gain, loss) in NettingPairs)
        {
            var net = v.Get(gain) - v.Get(loss);
            v.Lines[gain] = net > 0 ? net : 0;
            v.Lines[loss] = net < 0 ? -net : 0;
        }
        Aggregate(v, FinancialStatementLayout.ProfitLoss);
        return v;
    }

    private static void Aggregate(StatementValues v, IEnumerable<StatementLine> roots)
    {
        foreach (var r in roots) Sum(v, r);
        foreach (var r in roots.Where(r => r.Formula is not null)) v.Lines[r.Code] = Eval(v, r.Formula!);
    }

    private static decimal Sum(StatementValues v, StatementLine l)
    {
        if (l.Children.Count == 0) return l.Formula is null ? v.Get(l.Code) : 0;
        var s = l.Children.Where(c => !c.IsMemo).Sum(c => Sum(v, c));
        v.Lines[l.Code] = s;
        return s;
    }

    /// <summary>Formuła postaci „R.A-R.B” lub „R.C+R.D-R.E”, obliczana na wcześniej wyliczonych pozycjach (kolejność układu).</summary>
    private static decimal Eval(StatementValues v, string formula)
    {
        decimal total = 0;
        var sign = 1;
        var token = "";
        foreach (var ch in formula + "+")
        {
            if (ch is '+' or '-')
            {
                if (token.Length > 0) total += sign * v.Get(token);
                token = "";
                sign = ch == '+' ? 1 : -1;
            }
            else token += ch;
        }
        return total;
    }

    /// <summary>Rok poprzedni (do danych porównawczych).</summary>
    public async Task<FiscalYear?> PreviousYearAsync(Guid fiscalYearId, CancellationToken ct = default)
    {
        var fy = await _db.FiscalYears.AsNoTracking().FirstAsync(f => f.Id == fiscalYearId, ct);
        return await _db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == fy.CompanyId && f.EndDate == fy.StartDate.AddDays(-1), ct);
    }
}
