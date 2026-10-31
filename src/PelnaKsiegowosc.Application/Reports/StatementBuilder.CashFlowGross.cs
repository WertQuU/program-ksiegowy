using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>Wpływy i wydatki brutto działalności inwestycyjnej i finansowej (zał. nr 1 do UoR) — z rzeczywistych operacji pieniężnych.</summary>
public sealed class CashFlowGross
{
    public Dictionary<string, decimal> Amounts { get; } = new();
    public decimal Get(string code) => Amounts.GetValueOrDefault(code);
    internal void Add(string code, decimal amount) => Amounts[code] = Amounts.GetValueOrDefault(code) + amount;
    public decimal InvestingIn => Get("B.I.1") + Get("B.I.3") + Get("B.I.4");
    public decimal InvestingOut => Get("B.II.1") + Get("B.II.3") + Get("B.II.4");
    public decimal FinancingIn => Get("C.I.1") + Get("C.I.2") + Get("C.I.4");
    public decimal FinancingOut => Get("C.II.1") + Get("C.II.2") + Get("C.II.4") + Get("C.II.8") + Get("C.II.9");
    /// <summary>Wpływy (+) i wydatki (−) pieniężne zakwalifikowane do działalności operacyjnej.</summary>
    public decimal Operating { get; internal set; }
}

public sealed partial class StatementBuilder
{
    private static bool Under(string? line, string prefix) => line is not null && (line == prefix || line.StartsWith(prefix + "."));

    /// <summary>
    /// Klasyfikacja przepływów pieniężnych na podstawie operacji na kontach środków pieniężnych (pozycja aktywów B.III.1.c):
    /// przeciwstawne konto zapisu decyduje o rodzaju działalności — aktywa trwałe i środki trwałe w budowie (A.I, A.II, A.IV) —
    /// inwestycyjna; krótkoterminowe aktywa finansowe (B.III bez środków pieniężnych) i otrzymane odsetki/dywidendy — inwestycyjna;
    /// kapitał, należne wpłaty na kapitał, udziały własne, kredyty i pożyczki, zapłacone odsetki — finansowa. Zapłata lub wpływ
    /// rozrachunku z kontrahentem jest przypisywany proporcjonalnie do treści dokumentu źródłowego rozrachunku (np. faktura za
    /// środek trwały — wydatek inwestycyjny w części wartości netto; VAT — operacyjny). Pozostałe — operacyjna.
    /// </summary>
    public async Task<CashFlowGross> CashFlowGrossAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var (accounts, _) = await ChartAsync(fiscalYearId, ct);
        var byId = accounts.ToDictionary(a => a.Id);
        var byNumber = accounts.GroupBy(a => a.Number).ToDictionary(g => g.Key, g => g.First());
        bool Cash(Account a) => Under(a.BalanceDebitLine, "B.III.1.c");
        var entries = await _db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.FiscalYearId == fiscalYearId && e.Status == EntryStatus.Zaksiegowany && e.PostingDate <= asOf
                        && e.Kind != EntryKind.BilansOtwarcia && e.Kind != EntryKind.Zamkniecie)
            .ToListAsync(ct);
        var cashEntries = entries.Where(e => e.Lines.Any(l => byId.TryGetValue(l.AccountId, out var a) && Cash(a))).ToList();
        // treść dokumentów źródłowych rozrachunków (do podziału zapłat): rozrachunek → zapisy dokumentu
        var itemIds = cashEntries.SelectMany(e => e.Lines).Where(l => l.OpenItemId != null).Select(l => l.OpenItemId!.Value).Distinct().ToList();
        var items = await _db.OpenItems.AsNoTracking().Where(o => itemIds.Contains(o.Id)).Select(o => new { o.Id, o.DocumentId }).ToListAsync(ct);
        var docIds = items.Where(i => i.DocumentId != null).Select(i => i.DocumentId!.Value).Distinct().ToList();
        var docEntries = await _db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.SourceDocumentId != null && docIds.Contains(e.SourceDocumentId.Value) && e.Status == EntryStatus.Zaksiegowany).ToListAsync(ct);
        var itemDoc = items.Where(i => i.DocumentId != null).ToDictionary(i => i.Id, i => i.DocumentId!.Value);

        var g = new CashFlowGross();
        string Classify(Account a, bool inflow)
        {
            var line = a.BalanceDebitLine ?? a.BalanceCreditLine;
            if (Under(line, "A.I") || Under(line, "A.II") || Under(line, "A.IV")) return inflow ? "B.I.1" : "B.II.1";
            if (Under(line, "A.III") || (Under(line, "B.III") && !Under(line, "B.III.1.c"))) return inflow ? "B.I.3" : "B.II.3";
            if (a.ProfitLossLine is "R.G.I" or "R.G.II") return "B.I.3";
            if (a.ProfitLossLine is "R.D.I" && inflow) return "B.I.1";   // wpływ ze zbycia niefinansowych aktywów trwałych
            if (Under(line, "C") || Under(line, "P.A")) return inflow ? "C.I.1" : "C.II.2";
            if (Under(line, "D")) return inflow ? "C.I.4" : "C.II.1";
            if (Under(line, "P.B.II") || Under(line, "P.B.III.3.a") || Under(line, "P.B.III.3.b")) return inflow ? "C.I.2" : "C.II.4";
            if (a.ProfitLossLine is "R.H.I" && !inflow) return "C.II.8";
            return "OP";
        }
        foreach (var e in cashEntries)
        {
            var cashDelta = e.Lines.Where(l => byId.TryGetValue(l.AccountId, out var a) && Cash(a)).Sum(l => l.Debit - l.Credit);
            if (cashDelta == 0) continue; // przeniesienia między rachunkami / kasą
            var inflow = cashDelta > 0;
            var counter = e.Lines.Where(l => byId.TryGetValue(l.AccountId, out var a) && !Cash(a)).ToList();
            var counterTotal = counter.Sum(l => Math.Abs(l.Debit - l.Credit));
            if (counterTotal == 0) continue;
            foreach (var l in counter)
            {
                var share = Math.Abs(cashDelta) * Math.Abs(l.Debit - l.Credit) / counterTotal;
                var a = byId[l.AccountId];
                var cat = Classify(a, inflow);
                if (cat == "OP" && l.OpenItemId is { } oi && itemDoc.TryGetValue(oi, out var docId))
                {
                    // zapłata / wpływ rozrachunku — według treści dokumentu: aktywa trwałe (Wn) lub przychód ze zbycia aktywów (R.D.I, Ma)
                    var docLines = docEntries.Where(d => d.SourceDocumentId == docId).SelectMany(d => d.Lines).Where(x => x.OpenItemId != oi && byId.ContainsKey(x.AccountId)).ToList();
                    var docTotal = docLines.Sum(x => inflow ? x.Credit : x.Debit);
                    if (docTotal > 0)
                    {
                        decimal invest = 0;
                        foreach (var x in docLines)
                        {
                            var xa = byId[x.AccountId];
                            var xl = xa.BalanceDebitLine ?? xa.BalanceCreditLine;
                            var part = inflow ? x.Credit : x.Debit;
                            if (part == 0) continue;
                            if (!inflow && (Under(xl, "A.I") || Under(xl, "A.II") || Under(xl, "A.IV"))) invest += part;
                            if (inflow && xa.ProfitLossLine == "R.D.I") invest += part;
                        }
                        var investShare = Money.Round(share * invest / docTotal);
                        if (investShare != 0) g.Add(inflow ? "B.I.1" : "B.II.1", investShare);
                        g.Operating += (inflow ? 1 : -1) * (share - investShare);
                        continue;
                    }
                }
                if (cat == "OP") g.Operating += inflow ? share : -share;
                else g.Add(cat, Money.Round(share));
            }
        }
        return g;
    }
}
