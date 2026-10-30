using System.Xml.Linq;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Infrastructure.Xml;

/// <summary>Zakres sprawozdania: pełny (zał. 1 UoR), jednostka mała korzystająca z uproszczeń (zał. 5), jednostka mikro (zał. 4).</summary>
public enum StatementScope
{
    Inna,
    Mala,
    Mikro
}

/// <summary>
/// Uproszczone e-Sprawozdania: SFJMAZ(2) (CRWDE 2025/08/07/13818, jednostki małe, zał. nr 5 UoR) i SFJMIZ(2)
/// (CRWDE 2025/08/12/13821, jednostki mikro, zał. nr 4 UoR). Pozycje bilansu i RZiS są sumami pozycji pełnego układu
/// (zał. nr 1), więc księgi i mapowanie kont pozostają te same.
/// </summary>
public static partial class FinancialStatementXmlBuilder
{
    public static readonly XNamespace NsMala = "http://crd.gov.pl/wzor/2025/08/07/13818/";
    public static readonly XNamespace NsMikro = "http://crd.gov.pl/wzor/2025/08/12/13821/";
    public static readonly XNamespace Jma = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2025/07/31/eD/JednostkaMalaStruktury/";
    public static readonly XNamespace Jmi = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2025/07/31/eD/JednostkaMikroStruktury/";
    public const string SchemaFileMala = "Schemat_SFJMAZ(2)_v1-0E.xsd";
    public const string SchemaFileMikro = "Schemat_SFJMIZ(2)_v1-0E.xsd";

    /// <summary>Pozycja układu uproszczonego: nazwa elementu, pozycje pełnego układu (suma) i pozycje podrzędne.</summary>
    private sealed record Pos(string Name, string[] Codes, params Pos[] Children);

    private static Pos P(string name, string codes, params Pos[] children) => new(name, codes.Split(' ', StringSplitOptions.RemoveEmptyEntries), children);

    // Załącznik nr 5 — bilans jednostki małej
    private static readonly Pos[] SmallAssets =
    {
        P("A", "A",
            P("A_I", "A.I"),
            P("A_II", "A.II", P("A_II_1", "A.II.1"), P("A_II_2", "A.II.2")),
            P("A_III", "A.III"),
            P("A_IV", "A.IV", P("A_IV_1", "A.IV.1"), P("A_IV_2", "A.IV.3")),
            P("A_V", "A.V")),
        P("B", "B",
            P("B_I", "B.I"),
            P("B_II", "B.II", P("B_II_A", "B.II.3.a")),
            P("B_III", "B.III", P("B_III_A", "B.III.1", P("B_III_A_1", "B.III.1.c"))),
            P("B_IV", "B.IV")),
        P("C", "C"),
        P("D", "D"),
    };

    private static readonly Pos[] SmallLiabilities =
    {
        P("A", "P.A",
            P("A_I", "P.A.I"), P("A_II", "P.A.II"), P("A_III", "P.A.III"), P("A_IV", "P.A.IV"),
            P("A_V", "P.A.V"), P("A_VI", "P.A.VI"), P("A_VII", "P.A.VII")),
        P("B", "P.B",
            P("B_I", "P.B.I", P("B_I_1", "P.B.I.2")),
            P("B_II", "P.B.II", P("B_II_1", "P.B.II.3.a")),
            P("B_III", "P.B.III",
                P("B_III_A", "P.B.III.3.a"),
                P("B_III_B", "P.B.III.1.a P.B.III.2.a P.B.III.3.d"),
                P("B_III_C", "P.B.III.4")),
            P("B_IV", "P.B.IV")),
    };

    private static readonly Pos[] SmallProfitLoss =
    {
        P("A", "R.A", P("A_I", "R.A.I R.A.IV"), P("A_II", "R.A.II"), P("A_III", "R.A.III")),
        P("B", "R.B",
            P("B_I", "R.B.I"), P("B_II", "R.B.II"), P("B_III", "R.B.III"), P("B_IV", "R.B.V"), P("B_V", "R.B.VI"),
            P("B_VI", "R.B.IV R.B.VII R.B.VIII", P("B_VI_1", "R.B.VIII"))),
        P("C", "R.C"),
        P("D", "R.D", P("D_1", "R.D.III")),
        P("E", "R.E", P("E_1", "R.E.II")),
        P("F", "R.G", P("F_I", "R.G.I"), P("F_II", "R.G.II"), P("F_III", "R.G.III"), P("F_IV", "R.G.IV")),
        P("G", "R.H", P("G_I", "R.H.I"), P("G_II", "R.H.II"), P("G_III", "R.H.III")),
        P("H", "R.I"),
        P("I", "R.J R.K"),
        P("J", "R.L"),
    };

    private static readonly Pos[] SmallCalculation =
    {
        P("A", "K.A"), P("B", "K.B"), P("C", "K.D"), P("D", "K.E"), P("E", "K.F"),
        P("F", "K.G", P("F_1", "K.G.III")),
        P("G", "K.H", P("G_1", "K.H.II")),
        P("H", "K.J", P("H_I", "K.J.I"), P("H_II", "K.J.II"), P("H_III", "K.J.III"), P("H_IV", "K.J.IV")),
        P("I", "K.K", P("I_I", "K.K.I"), P("I_II", "K.K.II"), P("I_III", "K.K.III")),
        P("J", "K.L"),
        P("K", "K.M K.N"),
        P("L", "K.O"),
    };

    // Załącznik nr 4 — jednostka mikro
    private static readonly Pos[] MicroAssets =
    {
        P("A", "A"),
        P("B", "B", P("B_1", "B.I"), P("B_2", "B.II")),
        P("C", "C"),
        P("D", "D"),
    };

    private static readonly Pos[] MicroLiabilities =
    {
        P("A", "P.A", P("A_1", "P.A.I")),
        P("B", "P.B", P("B_1", "P.B.I"), P("B_2", "P.B.II.3.a P.B.III.3.a")),
    };

    private static readonly Pos[] MicroProfitLoss =
    {
        P("A", "R.A", P("A_1", "R.A.II")),
        P("B", "R.B", P("B_I", "R.B.I"), P("B_II", "R.B.II"), P("B_III", "R.B.V R.B.VI"), P("B_IV", "R.B.III R.B.IV R.B.VII R.B.VIII")),
        P("C", "R.D R.G", P("C_1", "R.D.III R.G.IV")),
        P("D", "R.E R.H", P("D_1", "R.E.II R.H.III")),
        P("E", "R.J R.K"),
        P("F", "R.L"),
    };

    private static XElement Node(XNamespace ns, string prefix, Pos p, StatementValues cur, StatementValues? prev) =>
        new(ns + (prefix + p.Name), Amounts(p.Codes.Sum(cur.Get), prev is null ? 0 : p.Codes.Sum(prev.Get)),
            p.Children.Select(c => Node(ns, prefix, c, cur, prev)));

    private static XElement Section(XNamespace ns, string name, decimal total, decimal totalPrev, string prefix, IEnumerable<Pos> items, StatementValues cur, StatementValues? prev) =>
        new(ns + name, Amounts(total, totalPrev), items.Select(p => Node(ns, prefix, p, cur, prev)));

    private static Result BuildSimplified(Company company, DateOnly from, DateOnly to, DateOnly prepared, StatementValues bs, StatementValues? bsPrev,
        StatementValues pl, StatementValues? plPrev, XsdValidator validator, Extras extras)
    {
        var micro = extras.Scope == StatementScope.Mikro;
        var ns = micro ? NsMikro : NsMala;
        var dom = micro ? Jmi : Jma;
        var notImplemented = new List<string>();
        var siedziba = Siedziba(company);
        XElement T(string n, params object?[] c) => new(ns + n, c);
        XElement F(string n, params object?[] c) => new(Dtsf + n, c);

        var policies = Paragraphs(extras.Policies, "Zgodnie z polityką rachunkowości jednostki.").ToList();
        var simplifications = string.IsNullOrWhiteSpace(extras.Simplifications)
            ? micro
                ? "Jednostka mikro stosuje uproszczenia z art. 28a, 37 ust. 10 i 48 ust. 1 pkt 1 UoR: bilans i rachunek zysków i strat w zakresie załącznika nr 4, bez informacji dodatkowej, z informacjami uzupełniającymi do bilansu; nie sporządza zestawienia zmian w kapitale i rachunku przepływów pieniężnych."
                : "Jednostka mała stosuje uproszczenia z art. 50 ust. 2a UoR: bilans, rachunek zysków i strat i informacja dodatkowa w zakresie załącznika nr 5; nie sporządza zestawienia zmian w kapitale i rachunku przepływów pieniężnych."
            : extras.Simplifications!.Trim();
        var intro = T(micro ? "InformacjeOgolneJednostkaMikro" : "WprowadzenieDoSprawozdaniaFinansowegoJednostkaMala",
            T("P_1",
                T("P_1A", F("NazwaFirmy", company.FullName), siedziba),
                P1B(ns, company),
                T("P_1C", Identifiers.NormalizeNip(company.Nip)),
                string.IsNullOrWhiteSpace(company.Krs) ? null : T("P_1D", company.Krs!.Trim())),
            T("P_3", F("DataOd", D(from)), F("DataDo", D(to))),
            T("P_4", simplifications),
            T("P_5", T("P_5A", "1"), T("P_5B", extras.GoingConcernThreatened ? "2" : "1"),
                extras.GoingConcernThreatened ? T("P_5C", extras.GoingConcern ?? "") : null),
            T("P_6",
                T("P_6A", policies[0]),
                T("P_6B", policies.Count > 1 ? policies[1] : "Wynik finansowy ustala się w układzie przyjętym w polityce rachunkowości."),
                T("P_6C", policies.Count > 2 ? string.Join(" ", policies.Skip(2)) :
                    $"Sprawozdanie sporządzono zgodnie z załącznikiem nr {(micro ? 4 : 5)} do ustawy o rachunkowości.")),
            T("P_7", M(extras.AverageEmployment)),
            T("P_8", extras.AuditRequired ? "1" : "2"));
        if (!string.IsNullOrWhiteSpace(extras.GoingConcern))
            intro.Add(T("P_9", F("NazwaPozycji", "Założenie kontynuacji działalności"), F("Opis", extras.GoingConcern)));

        var assets = Section(dom, "Aktywa", bs.Get("AKTYWA"), bsPrev?.Get("AKTYWA") ?? 0, "Aktywa_", micro ? MicroAssets : SmallAssets, bs, bsPrev);
        var liabilities = Section(dom, "Pasywa", bs.Get("PASYWA"), bsPrev?.Get("PASYWA") ?? 0, "Pasywa_", micro ? MicroLiabilities : SmallLiabilities, bs, bsPrev);

        XElement rzis;
        if (micro)
            rzis = T("RZiSJednostkaMikro", MicroProfitLoss.Select(p => Node(dom, "", p, pl, plPrev)));
        else if (extras.CalculationVariant is { } calc)
            rzis = T("RZiSJednostkaMala", new XElement(dom + "RZiSKalk", SmallCalculation.Select(p => Node(dom, "", p, calc, extras.CalculationVariantPrev))));
        else
            rzis = T("RZiSJednostkaMala", new XElement(dom + "RZiSPor", SmallProfitLoss.Select(p => Node(dom, "", p, pl, plPrev))));

        var code = micro ? "SprFinJednostkaMikroWZlotych" : "SprFinJednostkaMalaWZlotych";
        var system = micro ? "SFJMIZ (2)" : "SFJMAZ (2)";
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "Dokument",
                new XAttribute(XNamespace.Xmlns + "str", Str.NamespaceName), new XAttribute(XNamespace.Xmlns + "dtsf", Dtsf.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "jin", Jin.NamespaceName), new XAttribute(XNamespace.Xmlns + (micro ? "jmi" : "jma"), dom.NamespaceName),
                T("OpisDokumentu"),
                T("DaneDokumentu"),
                T("TrescDokumentu", new XAttribute("format", "text/xml"), new XAttribute("kodowanie", "XML"),
                    T(micro ? "JednostkaMikro" : "JednostkaMala",
                        T("Naglowek", F("OkresOd", D(from)), F("OkresDo", D(to)), F("DataSporzadzenia", D(prepared)),
                            new XElement(dom + "KodSprawozdania", new XAttribute("kodSystemowy", system), new XAttribute("wersjaSchemy", "1-0E"), code),
                            new XElement(dom + "WariantSprawozdania", "2")),
                        intro,
                        T(micro ? "BilansJednostkaMikro" : "BilansJednostkaMala", assets, liabilities),
                        rzis,
                        extras.Equity is null ? null : Equity(ns + "ZestZmianWKapitaleJednostkaInna", extras.Equity, extras.EquityPrev, notImplemented),
                        extras.CashFlow is null ? null : CashFlow(ns + "RachPrzeplywowJednostkaInna", extras.CashFlow, extras.CashFlowPrev, notImplemented),
                        micro
                            ? Notes(ns + "InformacjeUzupelniajaceDoBilansuJednostkaMikro", ns + "InformacjeUzupelniajaceDoBilansu", extras, notImplemented)
                            : Notes(ns + "DodatkoweInformacjeIObjasnieniaJednostkaMala", ns + "DodatkoweInformacjeIObjasnienia", extras, notImplemented)))));
        var validation = validator.Validate(doc, micro ? SchemaFileMikro : SchemaFileMala, $"e-Sprawozdanie {system.Replace(" ", "")}");
        notImplemented.Add("Podpisy kierownika jednostki i osoby sporządzającej (podpis kwalifikowany / profil zaufany / e-dowód) — w aplikacji e-Sprawozdania MF lub e-KRS");
        return new Result(doc, validation, notImplemented);
    }
}
