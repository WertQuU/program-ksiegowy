using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Reporting;

namespace PelnaKsiegowosc.Application.Reports;

/// <summary>
/// Klucz podziału kosztów rodzajowych (zespół 4, bez wartości sprzedanych towarów) na koszty wytworzenia, sprzedaży
/// i ogólnego zarządu — do RZiS w wariancie kalkulacyjnym, gdy jednostka nie prowadzi kont zespołu 5.
/// Wartości w procentach, suma = 100.
/// </summary>
public sealed record CostAllocationKey(decimal ProductionPercent, decimal SellingPercent, decimal AdministrationPercent)
{
    public const string SettingsKey = "rzis.klucz-podzialu";
    public static CostAllocationKey Default { get; } = new(60m, 10m, 30m);

    public void Validate()
    {
        if (ProductionPercent < 0 || SellingPercent < 0 || AdministrationPercent < 0)
            throw new DomainException("Klucz podziału kosztów nie może zawierać wartości ujemnych.");
        if (ProductionPercent + SellingPercent + AdministrationPercent != 100m)
            throw new DomainException("Suma klucza podziału kosztów (wytworzenie + sprzedaż + zarząd) musi wynosić 100%.");
    }

    public override string ToString() => $"wytworzenie {ProductionPercent:0.##}% / sprzedaż {SellingPercent:0.##}% / zarząd {AdministrationPercent:0.##}%";
}

/// <summary>Wiersz zestawienia zmian w kapitale (funduszu) własnym.</summary>
public sealed record EquityChangeRow(string Code, string Label, decimal Opening, decimal Increase, decimal Decrease, decimal Closing);

public sealed class EquityChanges
{
    public List<EquityChangeRow> Rows { get; } = new();
    public List<string> Warnings { get; } = new();
    public decimal Opening => Rows.Sum(r => r.Opening);
    public decimal Closing => Rows.Sum(r => r.Closing);
}

/// <summary>Wiersz rachunku przepływów pieniężnych (metoda pośrednia).</summary>
public sealed record CashFlowLine(string Code, string Label, decimal Amount, int Level = 1, bool IsTotal = false);

public sealed class CashFlowStatement
{
    public List<CashFlowLine> Lines { get; } = new();
    /// <summary>Różnica między przepływami netto a bilansową zmianą środków pieniężnych, niewyjaśniona udokumentowanymi korektami.</summary>
    public decimal Unreconciled { get; set; }
    public List<string> Warnings { get; } = new();
    public decimal Get(string code) => Lines.FirstOrDefault(l => l.Code == code)?.Amount ?? 0m;
}

public sealed partial class StatementBuilder
{
    /// <summary>
    /// RZiS w wariancie kalkulacyjnym. Przychody, pozostałe przychody/koszty operacyjne, przychody/koszty finansowe,
    /// podatek — jak w wariancie porównawczym (te same konta). Koszty rodzajowe (B.I–B.VII) dzielone kluczem na koszt
    /// wytworzenia (pomniejszony o zmianę stanu produktów i koszt wytworzenia na własne potrzeby), koszty sprzedaży
    /// i koszty ogólnego zarządu. Wynik netto jest identyczny jak w wariancie porównawczym (kontrola).
    /// </summary>
    public async Task<StatementValues> ProfitAndLossCalculationAsync(Guid fiscalYearId, DateOnly asOf, CostAllocationKey key, CancellationToken ct = default)
    {
        key.Validate();
        var r = await ProfitAndLossAsync(fiscalYearId, asOf, ct);
        var v = new StatementValues { AsOf = asOf };
        v.Warnings.AddRange(r.Warnings);
        v.Unmapped.AddRange(r.Unmapped);
        var byNature = new[] { "R.B.I", "R.B.II", "R.B.III", "R.B.IV", "R.B.V", "R.B.VI", "R.B.VII" }.Sum(r.Get);
        var selling = Math.Round(byNature * key.SellingPercent / 100m, 2, MidpointRounding.AwayFromZero);
        var admin = Math.Round(byNature * key.AdministrationPercent / 100m, 2, MidpointRounding.AwayFromZero);
        var production = byNature - selling - admin;
        v.Lines["K.A.I"] = r.Get("R.A.I");
        v.Lines["K.A.II"] = r.Get("R.A.IV");
        v.Lines["K.B.I"] = production - r.Get("R.A.II") - r.Get("R.A.III");
        v.Lines["K.B.II"] = r.Get("R.B.VIII");
        v.Lines["K.D"] = selling;
        v.Lines["K.E"] = admin;
        foreach (var (k, c) in new[]
                 {
                     ("K.G.I", "R.D.I"), ("K.G.II", "R.D.II"), ("K.G.III", "R.D.III"), ("K.G.IV", "R.D.IV"),
                     ("K.H.I", "R.E.I"), ("K.H.II", "R.E.II"), ("K.H.III", "R.E.III"),
                     ("K.J.I", "R.G.I"), ("K.J.II", "R.G.II"), ("K.J.III", "R.G.III"), ("K.J.IV", "R.G.IV"), ("K.J.V", "R.G.V"),
                     ("K.K.I", "R.H.I"), ("K.K.II", "R.H.II"), ("K.K.III", "R.H.III"), ("K.K.IV", "R.H.IV"),
                     ("K.M", "R.J"), ("K.N", "R.K")
                 })
            v.Lines[k] = r.Get(c);
        Aggregate(v, FinancialStatementLayout.ProfitLossCalculation);
        if (v.Get("K.O") != r.Get("R.L"))
            v.Warnings.Add($"Wynik netto wariantu kalkulacyjnego ({Money.Format(v.Get("K.O"))}) ≠ wynik wariantu porównawczego ({Money.Format(r.Get("R.L"))}).");
        return v;
    }

    private static readonly (string Code, string Label)[] EquityItems =
    {
        ("P.A.I", "Kapitał (fundusz) podstawowy"),
        ("P.A.II", "Kapitał (fundusz) zapasowy"),
        ("P.A.III", "Kapitał (fundusz) z aktualizacji wyceny"),
        ("P.A.IV", "Pozostałe kapitały (fundusze) rezerwowe"),
        ("P.A.V", "Zysk (strata) z lat ubiegłych"),
        ("P.A.VI", "Wynik netto"),
        ("P.A.VII", "Odpisy z zysku netto w ciągu roku obrotowego"),
    };

    /// <summary>
    /// Zestawienie zmian w kapitale (funduszu) własnym: stan z bilansu otwarcia, zwiększenia (obroty Ma) i zmniejszenia
    /// (obroty Wn) kont przypisanych do pozycji A.I–A.VII pasywów, stan końcowy zgodny z bilansem. Wynik netto: wynik roku
    /// z RZiS; pozostała zmiana poz. A.VI to rozliczenie wyniku z lat ubiegłych.
    /// </summary>
    public async Task<EquityChanges> EquityChangesAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default)
    {
        var (accounts, _) = await ChartAsync(fiscalYearId, ct);
        var lines = await LinesAsync(fiscalYearId, asOf, ct);
        var opening = await OpeningBalanceSheetAsync(fiscalYearId, ct);
        var closing = await BalanceSheetAsync(fiscalYearId, asOf, ct);
        var pl = await ProfitAndLossAsync(fiscalYearId, asOf, ct);
        var result = new EquityChanges();
        result.Warnings.AddRange(closing.Warnings.Where(w => w.StartsWith("Suma aktywów")));
        string? EquityLine(Account a) =>
            a.Type is AccountType.WynikoweKosztowe or AccountType.WynikowePrzychodowe or AccountType.WynikFinansowy or AccountType.Pozabilansowe ? null
            : (a.BalanceCreditLine ?? a.BalanceDebitLine) is { } l && l.StartsWith("P.A.") ? l : null;
        var mapped = accounts.Select(a => (a.Id, Line: EquityLine(a))).Where(x => x.Line is not null).ToDictionary(x => x.Id, x => x.Line!);
        var movements = lines.Where(l => l.Kind != EntryKind.BilansOtwarcia && mapped.ContainsKey(l.AccountId)).ToList();
        foreach (var (code, label) in EquityItems)
        {
            var o = opening.Get(code);
            var c = closing.Get(code);
            decimal inc, dec;
            if (code == "P.A.VI")
            {
                var net = pl.Get("R.L");
                var other = c - o - net;
                inc = (net > 0 ? net : 0) + (other > 0 ? other : 0);
                dec = (net < 0 ? -net : 0) + (other < 0 ? -other : 0);
            }
            else
            {
                var m = movements.Where(l => LineMatches(mapped[l.AccountId], code)).ToList();
                inc = m.Sum(x => x.Credit);
                dec = m.Sum(x => x.Debit);
                if (o + inc - dec != c)
                    result.Warnings.Add($"{label}: stan początkowy + zwiększenia − zmniejszenia ({Money.Format(o + inc - dec)}) ≠ stan z bilansu ({Money.Format(c)}) — sprawdź przypisanie kont do pozycji bilansu.");
            }
            if (o != 0 || inc != 0 || dec != 0 || c != 0 || code is "P.A.I" or "P.A.VI")
                result.Rows.Add(new EquityChangeRow(code, label, o, inc, dec, c));
        }
        return result;
    }

    private static bool LineMatches(string line, string code) => line == code || line.StartsWith(code + ".");

    /// <summary>
    /// Rachunek przepływów pieniężnych metodą pośrednią — z wyniku netto, amortyzacji oraz zmian pozycji bilansu
    /// (bilans otwarcia → bilans na dzień). Działalność inwestycyjna: zmiana aktywów trwałych (bez należności
    /// i rozliczeń długoterminowych) skorygowana o amortyzację i wynik ze zbycia; finansowa: kredyty, pożyczki,
    /// papiery dłużne, kapitał (wpłaty, wypłaty z zysku), należne wpłaty i udziały własne. Kontrola: przepływy netto = zmiana środków pieniężnych.
    /// </summary>
    /// <param name="otherAdjustments">Udokumentowane korekty działalności operacyjnej (A.II.10 — np. różnice kursowe od środków
    /// pieniężnych, operacje niepieniężne) z opisem; program nie wylicza ich automatycznie.</param>
    public async Task<CashFlowStatement> CashFlowAsync(Guid fiscalYearId, DateOnly asOf, CancellationToken ct = default,
        IReadOnlyList<(string Description, decimal Amount)>? otherAdjustments = null)
    {
        var o = await OpeningBalanceSheetAsync(fiscalYearId, ct);
        var c = await BalanceSheetAsync(fiscalYearId, asOf, ct);
        var pl = await ProfitAndLossAsync(fiscalYearId, asOf, ct);
        decimal D(string code) => c.Get(code) - o.Get(code);
        var cf = new CashFlowStatement();
        cf.Warnings.AddRange(c.Warnings.Concat(pl.Warnings));

        var net = pl.Get("R.L");
        var depreciation = pl.Get("R.B.I");
        var disposalResult = pl.Get("R.D.I") - pl.Get("R.E.I");
        var interest = pl.Get("R.H.I") - pl.Get("R.G.II");
        var dividends = pl.Get("R.G.I");
        // należności i zobowiązania krótkoterminowe zaklasyfikowane do działalności inwestycyjnej lub finansowej (kredyty, dłużne
        // papiery, inne zobowiązania finansowe, leasing finansowy, rozrachunki z tytułu dywidend, pożyczki udzielone itp.) — ich
        // zmiana nie jest korektą operacyjną (przepływy wykazane w częściach B i C)
        decimal nonOperatingLiabilities = 0, nonOperatingReceivables = 0;
        var (chartAccounts, chartParents) = await ChartAsync(fiscalYearId, ct);
        var nonOp = chartAccounts.Where(a => !chartParents.Contains(a.Id) && CategoryOf(a) != CashFlowCategory.Operacyjna).ToDictionary(a => a.Id);
        if (nonOp.Count > 0)
            foreach (var l in (await LinesAsync(fiscalYearId, asOf, ct)).Where(l => l.Kind != EntryKind.BilansOtwarcia && l.Kind != EntryKind.Zamkniecie && nonOp.ContainsKey(l.AccountId)))
            {
                var a = nonOp[l.AccountId];
                if (Under(a.BalanceCreditLine, "P.B.III") || (a.BalanceDebitLine is null && Under(a.BalanceCreditLine, "P.B.III"))) nonOperatingLiabilities += l.Credit - l.Debit;
                else if (Under(a.BalanceDebitLine, "B.II")) nonOperatingReceivables += l.Debit - l.Credit;
            }
        var ops = new List<(string, string, decimal)>
        {
            ("A.II.1", "Amortyzacja", depreciation),
            ("A.II.2", "Zyski (straty) z tytułu różnic kursowych", 0m), // uzupełniane niżej z wyceny środków pieniężnych i pozycji inwestycyjnych/finansowych
            ("A.II.3", "Odsetki i udziały w zyskach (dywidendy)", interest - dividends),
            ("A.II.4", "Zysk (strata) z działalności inwestycyjnej", -disposalResult),
            ("A.II.5", "Zmiana stanu rezerw", D("P.B.I")),
            ("A.II.6", "Zmiana stanu zapasów", -D("B.I")),
            ("A.II.7", "Zmiana stanu należności", -(D("B.II") + D("A.III")) + nonOperatingReceivables),
            ("A.II.8", "Zmiana stanu zobowiązań krótkoterminowych, z wyjątkiem pożyczek i kredytów", D("P.B.III") - nonOperatingLiabilities),
            ("A.II.9", "Zmiana stanu rozliczeń międzyokresowych", D("P.B.IV") - D("A.V") - D("B.IV")),
        };
        // działalność inwestycyjna i finansowa — wpływy i wydatki brutto z rzeczywistych operacji pieniężnych (zał. nr 1 do UoR)
        var g = await CashFlowGrossAsync(fiscalYearId, asOf, ct);
        // różnice kursowe niezrealizowane od pozycji inwestycyjnych i finansowych (wycena kredytów, pożyczek, aktywów finansowych)
        // są w wyniku, ale nie są przepływem — wyłączane w A.II.2 (zysk −, strata +). Wycena środków pieniężnych pozostaje
        // w działalności operacyjnej (struktura MF: G = F ± D) i jest wykazywana informacyjnie w E.1.
        var fxIndex = ops.FindIndex(x => x.Item1 == "A.II.2");
        ops[fxIndex] = ("A.II.2", "Zyski (straty) z tytułu różnic kursowych", -g.NonOperatingFx);
        var invNet = g.InvestingIn - g.InvestingOut;
        var finNet = g.FinancingIn - g.FinancingOut;
        // inne korekty (A.II.10) — wyłącznie udokumentowane przez użytkownika; różnica uzgodnienia nie jest „dopisywana” automatycznie
        var other = otherAdjustments?.Sum(x => x.Amount) ?? 0m;
        ops.Add(("A.II.10", "Inne korekty" + (otherAdjustments is { Count: > 0 } oa ? " (" + string.Join("; ", oa.Select(x => x.Description)) + ")" : ""), other));
        var opsTotal = ops.Sum(x => x.Item3);
        cf.Lines.Add(new CashFlowLine("A", "Przepływy środków pieniężnych z działalności operacyjnej", 0, 0));
        cf.Lines.Add(new CashFlowLine("A.I", "Zysk (strata) netto", net));
        cf.Lines.Add(new CashFlowLine("A.II", "Korekty razem", opsTotal));
        foreach (var (code, label, amount) in ops) cf.Lines.Add(new CashFlowLine(code, label, amount, 2));
        var opsNet = net + opsTotal;
        cf.Lines.Add(new CashFlowLine("A.III", "Przepływy pieniężne netto z działalności operacyjnej (I ± II)", opsNet, 1, true));

        void L(string code, string label, int level = 2) => cf.Lines.Add(new CashFlowLine(code, label, g.Get(code), level));
        cf.Lines.Add(new CashFlowLine("B", "Przepływy środków pieniężnych z działalności inwestycyjnej", 0, 0));
        cf.Lines.Add(new CashFlowLine("B.I", "Wpływy", g.InvestingIn));
        L("B.I.1", "Zbycie wartości niematerialnych i prawnych oraz rzeczowych aktywów trwałych");
        L("B.I.2", "Zbycie inwestycji w nieruchomości oraz wartości niematerialne i prawne");
        cf.Lines.Add(new CashFlowLine("B.I.3", "Z aktywów finansowych", new[] { "B.I.3.A", "B.I.3.B.1", "B.I.3.B.2", "B.I.3.B.3", "B.I.3.B.4", "B.I.3.B.5" }.Sum(g.Get), 2));
        L("B.I.3.A", "a) w jednostkach powiązanych", 3);
        cf.Lines.Add(new CashFlowLine("B.I.3.B", "b) w pozostałych jednostkach", new[] { "B.I.3.B.1", "B.I.3.B.2", "B.I.3.B.3", "B.I.3.B.4", "B.I.3.B.5" }.Sum(g.Get), 3));
        L("B.I.3.B.1", "– zbycie aktywów finansowych", 4);
        L("B.I.3.B.2", "– dywidendy i udziały w zyskach", 4);
        L("B.I.3.B.3", "– spłata udzielonych pożyczek długoterminowych", 4);
        L("B.I.3.B.4", "– odsetki", 4);
        L("B.I.3.B.5", "– inne wpływy z aktywów finansowych", 4);
        L("B.I.4", "Inne wpływy inwestycyjne");
        cf.Lines.Add(new CashFlowLine("B.II", "Wydatki", g.InvestingOut));
        L("B.II.1", "Nabycie wartości niematerialnych i prawnych oraz rzeczowych aktywów trwałych");
        L("B.II.2", "Inwestycje w nieruchomości oraz wartości niematerialne i prawne");
        cf.Lines.Add(new CashFlowLine("B.II.3", "Na aktywa finansowe", new[] { "B.II.3.A", "B.II.3.B.1", "B.II.3.B.2" }.Sum(g.Get), 2));
        L("B.II.3.A", "a) w jednostkach powiązanych", 3);
        cf.Lines.Add(new CashFlowLine("B.II.3.B", "b) w pozostałych jednostkach", g.Get("B.II.3.B.1") + g.Get("B.II.3.B.2"), 3));
        L("B.II.3.B.1", "– nabycie aktywów finansowych", 4);
        L("B.II.3.B.2", "– udzielone pożyczki długoterminowe", 4);
        L("B.II.4", "Inne wydatki inwestycyjne");
        cf.Lines.Add(new CashFlowLine("B.III", "Przepływy pieniężne netto z działalności inwestycyjnej (I − II)", invNet, 1, true));

        cf.Lines.Add(new CashFlowLine("C", "Przepływy środków pieniężnych z działalności finansowej", 0, 0));
        cf.Lines.Add(new CashFlowLine("C.I", "Wpływy", g.FinancingIn));
        L("C.I.1", "Wpływy netto z wydania udziałów (emisji akcji) i innych instrumentów kapitałowych oraz dopłat do kapitału");
        L("C.I.2", "Kredyty i pożyczki");
        L("C.I.3", "Emisja dłużnych papierów wartościowych");
        L("C.I.4", "Inne wpływy finansowe");
        cf.Lines.Add(new CashFlowLine("C.II", "Wydatki", g.FinancingOut));
        L("C.II.1", "Nabycie udziałów (akcji) własnych");
        L("C.II.2", "Dywidendy i inne wypłaty na rzecz właścicieli");
        L("C.II.3", "Inne, niż wypłaty na rzecz właścicieli, wydatki z tytułu podziału zysku");
        L("C.II.4", "Spłaty kredytów i pożyczek");
        L("C.II.5", "Wykup dłużnych papierów wartościowych");
        L("C.II.6", "Z tytułu innych zobowiązań finansowych");
        L("C.II.7", "Płatności zobowiązań z tytułu umów leasingu finansowego");
        L("C.II.8", "Odsetki");
        L("C.II.9", "Inne wydatki finansowe");
        cf.Lines.Add(new CashFlowLine("C.III", "Przepływy pieniężne netto z działalności finansowej (I − II)", finNet, 1, true));

        var total = opsNet + invNet + finNet;
        var cashOpen = o.Get("B.III.1.c");
        var cashClose = c.Get("B.III.1.c");
        cf.Lines.Add(new CashFlowLine("D", "Przepływy pieniężne netto razem (A.III ± B.III ± C.III)", total, 0, true));
        cf.Lines.Add(new CashFlowLine("E", "Bilansowa zmiana stanu środków pieniężnych, w tym:", cashClose - cashOpen, 0));
        cf.Lines.Add(new CashFlowLine("E.1", "– zmiana stanu środków pieniężnych z tytułu różnic kursowych", g.CashFx, 1));
        cf.Lines.Add(new CashFlowLine("F", "Środki pieniężne na początek okresu", cashOpen, 0));
        cf.Lines.Add(new CashFlowLine("G", "Środki pieniężne na koniec okresu (F ± D)", cashOpen + total, 0, true));
        cf.Unreconciled = cashClose - cashOpen - total;
        if (cf.Unreconciled != 0)
            cf.Warnings.Add($"NIEUZGODNIONE: przepływy netto ({Money.Format(total)}) ≠ bilansowa zmiana środków pieniężnych ({Money.Format(cashClose - cashOpen)}), różnica {Money.Format(cf.Unreconciled)} zł. " +
                            "Ustal przyczynę (klasyfikacja kont — kategoria RPP w planie kont, różnice kursowe od środków pieniężnych, operacje niepieniężne, kompensaty) i wprowadź udokumentowane korekty A.II.10; do tego czasu sprawozdanie nie może być oznaczone jako gotowe.");
        return cf;
    }
}
