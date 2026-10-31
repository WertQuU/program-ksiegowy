using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>
/// Wpływy i wydatki brutto działalności inwestycyjnej i finansowej (zał. nr 1 do UoR, metoda pośrednia — części B i C)
/// z rzeczywistych operacji pieniężnych. Kody pozycji jak w załączniku: B.I.1–B.I.4 (B.I.3.A — jednostki powiązane,
/// B.I.3.B.1–5 — pozostałe), B.II.1–B.II.4 (B.II.3.A, B.II.3.B.1–2), C.I.1–C.I.4, C.II.1–C.II.9.
/// </summary>
public sealed class CashFlowGross
{
    public static readonly string[] InvestingInCodes = { "B.I.1", "B.I.2", "B.I.3.A", "B.I.3.B.1", "B.I.3.B.2", "B.I.3.B.3", "B.I.3.B.4", "B.I.3.B.5", "B.I.4" };
    public static readonly string[] InvestingOutCodes = { "B.II.1", "B.II.2", "B.II.3.A", "B.II.3.B.1", "B.II.3.B.2", "B.II.4" };
    public static readonly string[] FinancingInCodes = { "C.I.1", "C.I.2", "C.I.3", "C.I.4" };
    public static readonly string[] FinancingOutCodes = { "C.II.1", "C.II.2", "C.II.3", "C.II.4", "C.II.5", "C.II.6", "C.II.7", "C.II.8", "C.II.9" };

    public Dictionary<string, decimal> Amounts { get; } = new();
    public decimal Get(string code) => Amounts.GetValueOrDefault(code);
    internal void Add(string code, decimal amount) { if (amount != 0) Amounts[code] = Amounts.GetValueOrDefault(code) + amount; }
    public decimal InvestingIn => InvestingInCodes.Sum(Get);
    public decimal InvestingOut => InvestingOutCodes.Sum(Get);
    public decimal FinancingIn => FinancingInCodes.Sum(Get);
    public decimal FinancingOut => FinancingOutCodes.Sum(Get);
    /// <summary>Wpływy (+) i wydatki (−) pieniężne zakwalifikowane do działalności operacyjnej (kontrolnie).</summary>
    public decimal Operating { get; internal set; }
    /// <summary>Przeniesienia między własnymi rachunkami i kasą (wyłączone z RPP).</summary>
    public decimal InternalTransfers { get; internal set; }
    /// <summary>Różnice kursowe z wyceny środków pieniężnych (zysk +, strata −) — zmiana stanu środków bez przepływu.</summary>
    public decimal CashFx { get; internal set; }
    /// <summary>Niezrealizowane różnice kursowe od pozycji inwestycyjnych i finansowych (zysk +, strata −) — bez przepływu.</summary>
    public decimal NonOperatingFx { get; internal set; }
}

public sealed partial class StatementBuilder
{
    private static bool Under(string? line, string prefix) => line is not null && (line == prefix || line.StartsWith(prefix + "."));

    /// <summary>Kategoria RPP konta: ustawiona na koncie albo wynikająca z pozycji bilansu / RZiS.</summary>
    public static CashFlowCategory CategoryOf(Account a)
    {
        if (a.CashFlow is { } explicitCategory) return explicitCategory;
        var line = a.BalanceDebitLine ?? a.BalanceCreditLine;
        var credit = a.BalanceCreditLine;
        if (Under(line, "A.I") || Under(line, "A.II")) return CashFlowCategory.RzeczoweAktywaTrwaleIWnip;
        if (Under(line, "A.IV.1") || Under(line, "A.IV.2")) return CashFlowCategory.InwestycjeWNieruchomosci;
        if (Under(line, "A.IV.3.a") || Under(line, "B.III.1.a")) return CashFlowCategory.AktywaFinansowePowiazane;
        if (Under(line, "A.IV.3") || Under(line, "B.III.1.b") || Under(line, "B.III.2")) return CashFlowCategory.AktywaFinansowe;
        if (Under(line, "A.IV.4")) return CashFlowCategory.InneInwestycyjne;
        if (Under(line, "C") || Under(line, "P.A")) return CashFlowCategory.KapitalWlasny;
        if (Under(line, "D")) return CashFlowCategory.UdzialyWlasne;
        if (Under(credit, "P.B.II.3.a") || Under(credit, "P.B.III.3.a")) return CashFlowCategory.KredytyIPozyczki;
        if (Under(credit, "P.B.II.3.b") || Under(credit, "P.B.III.3.b")) return CashFlowCategory.DluznePapieryWartosciowe;
        if (Under(credit, "P.B.II.3.c") || Under(credit, "P.B.III.3.c")) return CashFlowCategory.InneZobowiazaniaFinansowe;
        return CashFlowCategory.Operacyjna;
    }

    /// <summary>Pozycja RPP dla wpływu (inflow) lub wydatku z udziałem konta przeciwstawnego; null — działalność operacyjna.</summary>
    private static string? CashFlowCode(Account a, bool inflow)
    {
        // przychody i koszty finansowe — według RZiS (odsetki, dywidendy, zbycie aktywów)
        switch (a.ProfitLossLine)
        {
            case "R.D.I" when inflow: return "B.I.1";
            case "R.G.I" when inflow: return "B.I.3.B.2";
            case "R.G.II" when inflow: return "B.I.3.B.4";
            case "R.G.III" when inflow: return "B.I.3.B.1";
            case "R.H.I" when !inflow: return "C.II.8";
        }
        return CategoryOf(a) switch
        {
            CashFlowCategory.RzeczoweAktywaTrwaleIWnip => inflow ? "B.I.1" : "B.II.1",
            CashFlowCategory.InwestycjeWNieruchomosci => inflow ? "B.I.2" : "B.II.2",
            CashFlowCategory.AktywaFinansowePowiazane => inflow ? "B.I.3.A" : "B.II.3.A",
            CashFlowCategory.AktywaFinansowe => inflow ? "B.I.3.B.1" : "B.II.3.B.1",
            CashFlowCategory.PozyczkiUdzielone => inflow ? "B.I.3.B.3" : "B.II.3.B.2",
            CashFlowCategory.InneInwestycyjne => inflow ? "B.I.4" : "B.II.4",
            CashFlowCategory.KapitalWlasny => inflow ? "C.I.1" : "C.II.2",
            CashFlowCategory.WyplatyNaRzeczWlascicieli => inflow ? "C.I.4" : "C.II.2",
            CashFlowCategory.InneWydatkiZPodzialuZysku => inflow ? "C.I.4" : "C.II.3",
            CashFlowCategory.UdzialyWlasne => inflow ? "C.I.4" : "C.II.1",
            CashFlowCategory.KredytyIPozyczki => inflow ? "C.I.2" : "C.II.4",
            CashFlowCategory.DluznePapieryWartosciowe => inflow ? "C.I.3" : "C.II.5",
            CashFlowCategory.InneZobowiazaniaFinansowe => inflow ? "C.I.4" : "C.II.6",
            CashFlowCategory.LeasingFinansowy => inflow ? "C.I.4" : "C.II.7",
            CashFlowCategory.InneFinansowe => inflow ? "C.I.4" : "C.II.9",
            _ => null
        };
    }

    /// <summary>
    /// Klasyfikacja przepływów na podstawie operacji na kontach środków pieniężnych (B.III.1.c). Wpływy (Wn konta pieniężnego)
    /// i wydatki (Ma) każdego zapisu analizuje się odrębnie — bez kompensowania: wpływy rozdziela się na konta przeciwstawne
    /// uznawane (Ma), wydatki — na konta obciążane (Wn), proporcjonalnie do kwot. Część wpływu pokryta wydatkiem pieniężnym tego
    /// samego zapisu bez kont przeciwstawnych to przeniesienie między własnymi rachunkami (wyłączone). Zapłata/wpływ rozrachunku
    /// z kontrahentem — według treści dokumentu źródłowego rozrachunku (np. faktura za środek trwały: wartość netto — inwestycyjnie,
    /// VAT — operacyjnie). Kategorie kont: pozycja bilansu/RZiS albo kategoria RPP ustawiona na koncie.
    /// </summary>
    public async Task<CashFlowGross> CashFlowGrossAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var (accounts, _) = await ChartAsync(fiscalYearId, ct);
        var byId = accounts.ToDictionary(a => a.Id);
        bool Cash(Guid id) => byId.TryGetValue(id, out var a) && Under(a.BalanceDebitLine, "B.III.1.c");
        var entries = await _db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.FiscalYearId == fiscalYearId && e.Status == EntryStatus.Zaksiegowany && e.PostingDate <= asOf
                        && e.Kind != EntryKind.BilansOtwarcia && e.Kind != EntryKind.Zamkniecie)
            .ToListAsync(ct);
        var cashEntries = entries.Where(e => e.Lines.Any(l => Cash(l.AccountId))).ToList();
        var itemIds = cashEntries.SelectMany(e => e.Lines).Where(l => l.OpenItemId != null).Select(l => l.OpenItemId!.Value).Distinct().ToList();
        var items = await _db.OpenItems.AsNoTracking().Where(o => itemIds.Contains(o.Id)).Select(o => new { o.Id, o.DocumentId }).ToListAsync(ct);
        var docIds = items.Where(i => i.DocumentId != null).Select(i => i.DocumentId!.Value).Distinct().ToList();
        var docEntries = await _db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.SourceDocumentId != null && docIds.Contains(e.SourceDocumentId.Value) && e.Status == EntryStatus.Zaksiegowany).ToListAsync(ct);
        var itemDoc = items.Where(i => i.DocumentId != null).ToDictionary(i => i.Id, i => i.DocumentId!.Value);

        var posting = await new Setup.SettingsService(_db, new SystemClock()).GetAsync(accounts.FirstOrDefault()?.CompanyId ?? Guid.Empty,
            Templates.PostingSettings.SettingKey, () => new Templates.PostingSettings(), ct);
        var fxAccounts = accounts.Where(a => a.Number == posting.FxGains || a.Number == posting.FxLosses
                                             || a.Number.StartsWith(posting.FxGains + "-") || a.Number.StartsWith(posting.FxLosses + "-")).Select(a => a.Id).ToHashSet();
        var g = new CashFlowGross();
        // niezrealizowane różnice kursowe od pozycji inwestycyjnych i finansowych (zapisy bez środków pieniężnych: wyłącznie konta
        // różnic kursowych i konta o kategorii innej niż operacyjna) — korekta A.II.2
        foreach (var e in entries.Where(e => !e.Lines.Any(l => Cash(l.AccountId))))
        {
            var fx = e.Lines.Where(l => fxAccounts.Contains(l.AccountId)).ToList();
            if (fx.Count == 0) continue;
            var others = e.Lines.Where(l => !fxAccounts.Contains(l.AccountId) && byId.ContainsKey(l.AccountId)).ToList();
            if (others.Count > 0 && others.All(l => CategoryOf(byId[l.AccountId]) != CashFlowCategory.Operacyjna))
                g.NonOperatingFx += fx.Sum(l => l.Credit - l.Debit);
        }
        void Allocate(JournalLine counter, decimal amount, bool inflow)
        {
            var a = byId[counter.AccountId];
            var code = CashFlowCode(a, inflow);
            if (code is null && counter.OpenItemId is { } oi && itemDoc.TryGetValue(oi, out var docId))
            {
                // rozrachunek — treść dokumentu: konta uznawane (wpływ) / obciążane (wydatek) w dokumencie, poza samym rozrachunkiem
                var docLines = docEntries.Where(d => d.SourceDocumentId == docId).SelectMany(d => d.Lines)
                    .Where(x => x.OpenItemId != oi && byId.ContainsKey(x.AccountId) && (inflow ? x.Credit : x.Debit) != 0).ToList();
                var docTotal = docLines.Sum(x => inflow ? x.Credit : x.Debit);
                if (docTotal > 0)
                {
                    decimal allocated = 0;
                    foreach (var grp in docLines.GroupBy(x => CashFlowCode(byId[x.AccountId], inflow)))
                    {
                        if (grp.Key is null) continue;
                        var part = Money.Round(amount * grp.Sum(x => inflow ? x.Credit : x.Debit) / docTotal);
                        g.Add(grp.Key, part);
                        allocated += part;
                    }
                    g.Operating += (inflow ? 1 : -1) * (amount - allocated);
                    return;
                }
            }
            if (code is null) g.Operating += inflow ? amount : -amount;
            else g.Add(code, Money.Round(amount));
        }
        foreach (var e in cashEntries)
        {
            // wycena środków pieniężnych w walutach (konta pieniężne i wyłącznie konta różnic kursowych) — bez przepływu
            var nonCashLines = e.Lines.Where(l => !Cash(l.AccountId) && byId.ContainsKey(l.AccountId)).ToList();
            if (nonCashLines.Count > 0 && nonCashLines.All(l => fxAccounts.Contains(l.AccountId)))
            {
                var fx = e.Lines.Where(l => Cash(l.AccountId)).Sum(l => l.Debit - l.Credit);
                g.CashFx += fx;
                g.Operating += fx;
                continue;
            }
            var inflow = e.Lines.Where(l => Cash(l.AccountId)).Sum(l => l.Debit);
            var outflow = e.Lines.Where(l => Cash(l.AccountId)).Sum(l => l.Credit);
            var counterCredits = e.Lines.Where(l => !Cash(l.AccountId) && byId.ContainsKey(l.AccountId) && l.Credit != 0).ToList();
            var counterDebits = e.Lines.Where(l => !Cash(l.AccountId) && byId.ContainsKey(l.AccountId) && l.Debit != 0).ToList();
            var creditTotal = counterCredits.Sum(l => l.Credit);
            var debitTotal = counterDebits.Sum(l => l.Debit);
            // wpływy finansowane kontami przeciwstawnymi; reszta wpływu — przeniesienie z innego własnego rachunku/kasy
            var realIn = Math.Min(inflow, creditTotal);
            var realOut = Math.Min(outflow, debitTotal);
            g.InternalTransfers += inflow - realIn;
            if (realIn > 0) foreach (var l in counterCredits) Allocate(l, realIn * l.Credit / creditTotal, true);
            if (realOut > 0) foreach (var l in counterDebits) Allocate(l, realOut * l.Debit / debitTotal, false);
        }
        return g;
    }
}
