namespace PelnaKsiegowosc.Domain.Reporting;

public sealed class StatementLine
{
    public required string Code { get; init; }
    public required string Label { get; init; }
    public string? ParentCode { get; init; }
    public int Depth { get; init; }
    /// <summary>Wiersz wyliczany formułą (np. RZiS C = A − B) — nie sumuje dzieci.</summary>
    public string? Formula { get; init; }
    /// <summary>Wiersz informacyjny „w tym” — nie wchodzi do sumy rodzica.</summary>
    public bool IsMemo { get; init; }
    public List<StatementLine> Children { get; } = new();
}

/// <summary>
/// Układ bilansu i rachunku zysków i strat (wariant porównawczy) wg załącznika nr 1 do ustawy o rachunkowości
/// (jednostki inne niż banki, zakłady ubezpieczeń). Kody: aktywa „A…/B…/C/D”, pasywa „P.A…/P.B…”, RZiS „R.A…” (porównawczy) i „K.A…” (kalkulacyjny).
/// Treść pozycji odtworzona według załącznika — WYMAGA porównania z aktualnym tekstem jednolitym ustawy.
/// </summary>
public static class FinancialStatementLayout
{
    private const string AssetsText = """
A|Aktywa trwałe
A.I|Wartości niematerialne i prawne
A.I.1|Koszty zakończonych prac rozwojowych
A.I.2|Wartość firmy
A.I.3|Inne wartości niematerialne i prawne
A.I.4|Zaliczki na wartości niematerialne i prawne
A.II|Rzeczowe aktywa trwałe
A.II.1|Środki trwałe
A.II.1.a|grunty (w tym prawo użytkowania wieczystego gruntu)
A.II.1.b|budynki, lokale, prawa do lokali i obiekty inżynierii lądowej i wodnej
A.II.1.c|urządzenia techniczne i maszyny
A.II.1.d|środki transportu
A.II.1.e|inne środki trwałe
A.II.2|Środki trwałe w budowie
A.II.3|Zaliczki na środki trwałe w budowie
A.III|Należności długoterminowe
A.III.1|Od jednostek powiązanych
A.III.2|Od pozostałych jednostek, w których jednostka posiada zaangażowanie w kapitale
A.III.3|Od pozostałych jednostek
A.IV|Inwestycje długoterminowe
A.IV.1|Nieruchomości
A.IV.2|Wartości niematerialne i prawne
A.IV.3|Długoterminowe aktywa finansowe
A.IV.3.a|w jednostkach powiązanych
A.IV.3.b|w pozostałych jednostkach, w których jednostka posiada zaangażowanie w kapitale
A.IV.3.c|w pozostałych jednostkach
A.IV.4|Inne inwestycje długoterminowe
A.V|Długoterminowe rozliczenia międzyokresowe
A.V.1|Aktywa z tytułu odroczonego podatku dochodowego
A.V.2|Inne rozliczenia międzyokresowe
B|Aktywa obrotowe
B.I|Zapasy
B.I.1|Materiały
B.I.2|Półprodukty i produkty w toku
B.I.3|Produkty gotowe
B.I.4|Towary
B.I.5|Zaliczki na dostawy i usługi
B.II|Należności krótkoterminowe
B.II.1|Należności od jednostek powiązanych
B.II.2|Należności od pozostałych jednostek, w których jednostka posiada zaangażowanie w kapitale
B.II.3|Należności od pozostałych jednostek
B.II.3.a|z tytułu dostaw i usług
B.II.3.b|z tytułu podatków, dotacji, ceł, ubezpieczeń społecznych i zdrowotnych oraz innych tytułów publicznoprawnych
B.II.3.c|inne
B.II.3.d|dochodzone na drodze sądowej
B.III|Inwestycje krótkoterminowe
B.III.1|Krótkoterminowe aktywa finansowe
B.III.1.a|w jednostkach powiązanych
B.III.1.b|w pozostałych jednostkach
B.III.1.c|środki pieniężne i inne aktywa pieniężne
B.III.2|Inne inwestycje krótkoterminowe
B.IV|Krótkoterminowe rozliczenia międzyokresowe
C|Należne wpłaty na kapitał (fundusz) podstawowy
D|Udziały (akcje) własne
""";

    private const string LiabilitiesText = """
P.A|Kapitał (fundusz) własny
P.A.I|Kapitał (fundusz) podstawowy
P.A.II|Kapitał (fundusz) zapasowy
P.A.III|Kapitał (fundusz) z aktualizacji wyceny
P.A.IV|Pozostałe kapitały (fundusze) rezerwowe
P.A.V|Zysk (strata) z lat ubiegłych
P.A.VI|Zysk (strata) netto
P.A.VII|Odpisy z zysku netto w ciągu roku obrotowego (wielkość ujemna)
P.B|Zobowiązania i rezerwy na zobowiązania
P.B.I|Rezerwy na zobowiązania
P.B.I.1|Rezerwa z tytułu odroczonego podatku dochodowego
P.B.I.2|Rezerwa na świadczenia emerytalne i podobne
P.B.I.3|Pozostałe rezerwy
P.B.II|Zobowiązania długoterminowe
P.B.II.1|Wobec jednostek powiązanych
P.B.II.2|Wobec pozostałych jednostek, w których jednostka posiada zaangażowanie w kapitale
P.B.II.3|Wobec pozostałych jednostek
P.B.II.3.a|kredyty i pożyczki
P.B.II.3.b|z tytułu emisji dłużnych papierów wartościowych
P.B.II.3.c|inne zobowiązania finansowe
P.B.II.3.d|zobowiązania wekslowe
P.B.II.3.e|inne
P.B.III|Zobowiązania krótkoterminowe
P.B.III.1|Zobowiązania wobec jednostek powiązanych
P.B.III.1.a|z tytułu dostaw i usług
P.B.III.1.b|inne
P.B.III.2|Zobowiązania wobec pozostałych jednostek, w których jednostka posiada zaangażowanie w kapitale
P.B.III.2.a|z tytułu dostaw i usług
P.B.III.2.b|inne
P.B.III.3|Zobowiązania wobec pozostałych jednostek
P.B.III.3.a|kredyty i pożyczki
P.B.III.3.b|z tytułu emisji dłużnych papierów wartościowych
P.B.III.3.c|inne zobowiązania finansowe
P.B.III.3.d|z tytułu dostaw i usług
P.B.III.3.e|zaliczki otrzymane na dostawy i usługi
P.B.III.3.f|zobowiązania wekslowe
P.B.III.3.g|z tytułu podatków, ceł, ubezpieczeń społecznych i zdrowotnych oraz innych tytułów publicznoprawnych
P.B.III.3.h|z tytułu wynagrodzeń
P.B.III.3.i|inne
P.B.III.4|Fundusze specjalne
P.B.IV|Rozliczenia międzyokresowe
P.B.IV.1|Ujemna wartość firmy
P.B.IV.2|Inne rozliczenia międzyokresowe
""";

    private const string ProfitLossText = """
R.A|Przychody netto ze sprzedaży i zrównane z nimi
R.A.I|Przychody netto ze sprzedaży produktów
R.A.II|Zmiana stanu produktów (zwiększenie — wartość dodatnia, zmniejszenie — wartość ujemna)
R.A.III|Koszt wytworzenia produktów na własne potrzeby jednostki
R.A.IV|Przychody netto ze sprzedaży towarów i materiałów
R.B|Koszty działalności operacyjnej
R.B.I|Amortyzacja
R.B.II|Zużycie materiałów i energii
R.B.III|Usługi obce
R.B.IV|Podatki i opłaty
R.B.V|Wynagrodzenia
R.B.VI|Ubezpieczenia społeczne i inne świadczenia
R.B.VII|Pozostałe koszty rodzajowe
R.B.VIII|Wartość sprzedanych towarów i materiałów
R.C|Zysk (strata) ze sprzedaży (A − B)|=R.A-R.B
R.D|Pozostałe przychody operacyjne
R.D.I|Zysk z tytułu rozchodu niefinansowych aktywów trwałych
R.D.II|Dotacje
R.D.III|Aktualizacja wartości aktywów niefinansowych
R.D.IV|Inne przychody operacyjne
R.E|Pozostałe koszty operacyjne
R.E.I|Strata z tytułu rozchodu niefinansowych aktywów trwałych
R.E.II|Aktualizacja wartości aktywów niefinansowych
R.E.III|Inne koszty operacyjne
R.F|Zysk (strata) z działalności operacyjnej (C + D − E)|=R.C+R.D-R.E
R.G|Przychody finansowe
R.G.I|Dywidendy i udziały w zyskach
R.G.II|Odsetki
R.G.III|Zysk z tytułu rozchodu aktywów finansowych
R.G.IV|Aktualizacja wartości aktywów finansowych
R.G.V|Inne
R.H|Koszty finansowe
R.H.I|Odsetki
R.H.II|Strata z tytułu rozchodu aktywów finansowych
R.H.III|Aktualizacja wartości aktywów finansowych
R.H.IV|Inne
R.I|Zysk (strata) brutto (F + G − H)|=R.F+R.G-R.H
R.J|Podatek dochodowy
R.K|Pozostałe obowiązkowe zmniejszenia zysku (zwiększenia straty)
R.L|Zysk (strata) netto (I − J − K)|=R.I-R.J-R.K
""";

    private const string CalculationText = """
K.A|Przychody netto ze sprzedaży produktów, towarów i materiałów
K.A.I|Przychody netto ze sprzedaży produktów
K.A.II|Przychody netto ze sprzedaży towarów i materiałów
K.B|Koszty sprzedanych produktów, towarów i materiałów
K.B.I|Koszt wytworzenia sprzedanych produktów
K.B.II|Wartość sprzedanych towarów i materiałów
K.C|Zysk (strata) brutto ze sprzedaży (A − B)|=K.A-K.B
K.D|Koszty sprzedaży
K.E|Koszty ogólnego zarządu
K.F|Zysk (strata) ze sprzedaży (C − D − E)|=K.C-K.D-K.E
K.G|Pozostałe przychody operacyjne
K.G.I|Zysk z tytułu rozchodu niefinansowych aktywów trwałych
K.G.II|Dotacje
K.G.III|Aktualizacja wartości aktywów niefinansowych
K.G.IV|Inne przychody operacyjne
K.H|Pozostałe koszty operacyjne
K.H.I|Strata z tytułu rozchodu niefinansowych aktywów trwałych
K.H.II|Aktualizacja wartości aktywów niefinansowych
K.H.III|Inne koszty operacyjne
K.I|Zysk (strata) z działalności operacyjnej (F + G − H)|=K.F+K.G-K.H
K.J|Przychody finansowe
K.J.I|Dywidendy i udziały w zyskach
K.J.II|Odsetki
K.J.III|Zysk z tytułu rozchodu aktywów finansowych
K.J.IV|Aktualizacja wartości aktywów finansowych
K.J.V|Inne
K.K|Koszty finansowe
K.K.I|Odsetki
K.K.II|Strata z tytułu rozchodu aktywów finansowych
K.K.III|Aktualizacja wartości aktywów finansowych
K.K.IV|Inne
K.L|Zysk (strata) brutto (I + J − K)|=K.I+K.J-K.K
K.M|Podatek dochodowy
K.N|Pozostałe obowiązkowe zmniejszenia zysku (zwiększenia straty)
K.O|Zysk (strata) netto (L − M − N)|=K.L-K.M-K.N
""";

    public static readonly IReadOnlyList<StatementLine> Assets = Parse(AssetsText, null);
    public static readonly IReadOnlyList<StatementLine> Liabilities = Parse(LiabilitiesText, "P");
    public static readonly IReadOnlyList<StatementLine> ProfitLoss = Parse(ProfitLossText, "R");
    /// <summary>RZiS w wariancie kalkulacyjnym (kody „K…”).</summary>
    public static readonly IReadOnlyList<StatementLine> ProfitLossCalculation = Parse(CalculationText, "K");

    public static readonly IReadOnlyDictionary<string, StatementLine> All =
        Flatten(Assets).Concat(Flatten(Liabilities)).Concat(Flatten(ProfitLoss)).ToDictionary(l => l.Code);

    public static bool IsLeaf(string code) => All.TryGetValue(code, out var l) && l.Children.Count == 0 && l.Formula is null;

    public static IEnumerable<StatementLine> Flatten(IEnumerable<StatementLine> roots)
    {
        foreach (var r in roots)
        {
            yield return r;
            foreach (var c in Flatten(r.Children)) yield return c;
        }
    }

    private static List<StatementLine> Parse(string text, string? prefix)
    {
        var roots = new List<StatementLine>();
        var byCode = new Dictionary<string, StatementLine>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('|');
            var code = parts[0];
            var segs = code.Split('.');
            var skip = prefix is null ? 0 : 1;
            var depth = segs.Length - skip - 1;
            var parentCode = segs.Length - skip > 1 ? string.Join('.', segs[..^1]) : null;
            var line = new StatementLine
            {
                Code = code,
                Label = parts[1],
                ParentCode = parentCode,
                Depth = depth,
                Formula = parts.Length > 2 ? parts[2].TrimStart('=') : null
            };
            byCode[code] = line;
            if (parentCode is not null && byCode.TryGetValue(parentCode, out var parent)) parent.Children.Add(line);
            else roots.Add(line);
        }
        return roots;
    }
}
