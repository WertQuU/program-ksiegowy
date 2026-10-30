using System.Globalization;
using System.Xml.Linq;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Reporting;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Infrastructure.Xml;

/// <summary>
/// Sprawozdanie finansowe w postaci elektronicznej (art. 45 ust. 1f–1g UoR) — oficjalna struktura SFJINZ(2)
/// (CRWDE 2025/08/07/13817; jednostki inne niż banki i zakłady ubezpieczeń, dane w złotych; dla sprawozdań sporządzanych
/// od 1.01.2026). Zawiera: wprowadzenie, bilans, RZiS (porównawczy lub kalkulacyjny), zestawienie zmian w kapitale,
/// rachunek przepływów (metoda pośrednia), informację dodatkową (plik PDF) i informację dotyczącą podatku dochodowego.
/// Struktura SFJINZ może być stosowana także przez jednostki małe i mikro, które nie korzystają z uproszczeń.
/// </summary>
public static partial class FinancialStatementXmlBuilder
{
    public static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2025/08/07/13817/";
    public static readonly XNamespace Jin = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2025/07/31/eD/JednostkaInnaStruktury/";
    public static readonly XNamespace Dtsf = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2025/07/31/eD/StrukturyDanychSprFin/";
    public static readonly XNamespace Str = "http://crd.gov.pl/xml/schematy/struktura/2009/11/16/";
    public const string SchemaFile = "Schemat_SFJINZ(2)_v1-0E.xsd";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Result(XDocument Document, XsdValidationResult Validation, IReadOnlyList<string> NotImplemented)
    {
        public string Status => Validation.IsValid ? "ZWALIDOWANY ze schematem XSD" : "WYGENEROWANY — NIEZWALIDOWANY";
    }

    /// <summary>Informacja dotycząca podatku dochodowego (rozliczenie różnicy między podstawą opodatkowania a wynikiem).</summary>
    public sealed record IncomeTaxInfo(decimal ProfitBeforeTax, decimal ExemptRevenue, decimal NonTaxableRevenue, decimal TaxableRevenuePriorBooks,
        decimal NonDeductibleCosts, decimal NotYetDeductibleCosts, decimal DeductibleCostsPriorBooks, decimal LossesDeducted, decimal OtherChanges,
        decimal TaxBase, decimal Tax);

    /// <summary>Części sprawozdania dodatkowe względem bilansu i RZiS porównawczego.</summary>
    public sealed record Extras(
        StatementValues? CalculationVariant = null, StatementValues? CalculationVariantPrev = null,
        EquityChanges? Equity = null, EquityChanges? EquityPrev = null,
        CashFlowStatement? CashFlow = null, CashFlowStatement? CashFlowPrev = null,
        string? GoingConcern = null, string? Policies = null,
        byte[]? NotesPdf = null, string NotesFileName = "informacja-dodatkowa.pdf",
        decimal AverageEmployment = 0m, bool AuditRequired = false, IncomeTaxInfo? IncomeTax = null, IncomeTaxInfo? IncomeTaxPrev = null,
        bool GoingConcernThreatened = false, StatementScope Scope = StatementScope.Inna, string? Simplifications = null);

    public static string FormaPrawna(LegalForm f) => f switch
    {
        LegalForm.SpolkaZOgraniczonaOdpowiedzialnoscia => "17",
        LegalForm.ProstaSpolkaAkcyjna => "PA",
        LegalForm.SpolkaAkcyjna => "16",
        LegalForm.SpolkaKomandytowoAkcyjna => "21",
        LegalForm.SpolkaKomandytowa => "20",
        LegalForm.SpolkaJawna => "18",
        LegalForm.SpolkaPartnerska => "15",
        LegalForm.SpolkaCywilna => "19",
        LegalForm.DzialalnoscGospodarczaOsobyFizycznej => "OG",
        LegalForm.Fundacja => "48",
        LegalForm.Stowarzyszenie => "55",
        _ => ""
    };

    /// <summary>„A.II.1.a” → „A_II_1_A” (nazwy elementów struktury MF).</summary>
    public static string ElementSuffix(string code) =>
        string.Join("_", (code.StartsWith("P.") || code.StartsWith("R.") || code.StartsWith("K.") ? code[2..] : code).Split('.')
            .Select(s => s.Length == 1 && char.IsLower(s[0]) ? s.ToUpperInvariant() : s));

    private static string M(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", Inv);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    private static XElement[] Amounts(decimal a, decimal b) => new[] { new XElement(Dtsf + "KwotaA", M(a)), new XElement(Dtsf + "KwotaB", M(b)) };

    private static XElement Tree(StatementLine line, Func<StatementLine, string> name, StatementValues cur, StatementValues? prev)
    {
        var e = new XElement(Jin + name(line), Amounts(cur.Get(line.Code), prev?.Get(line.Code) ?? 0));
        foreach (var c in line.Children) e.Add(Tree(c, name, cur, prev));
        return e;
    }

    private static IEnumerable<string> Paragraphs(string? text, string fallback)
    {
        var p = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 0).ToList();
        return p.Count > 0 ? p : new[] { fallback };
    }

    private static XElement P1B(XNamespace ns, Company company)
    {
        var forma = FormaPrawna(company.LegalForm);
        return forma.Length > 0
            ? new XElement(ns + "P_1B", new XElement(ns + "FormaPrawna", forma))
            : new XElement(ns + "P_1B", new XElement(ns + "InneWybor", "1"), new XElement(ns + "InneNazwa", LegalForms.Name(company.LegalForm)));
    }

    private static XElement Siedziba(Company company)
    {
        XElement F(string n, params object?[] c) => new(Dtsf + n, c);
        if (string.IsNullOrWhiteSpace(company.Voivodeship) || string.IsNullOrWhiteSpace(company.County) || string.IsNullOrWhiteSpace(company.Commune))
            throw new DomainException("Uzupełnij województwo, powiat i gminę siedziby w danych jednostki — są wymagane w e-Sprawozdaniu.");
        return F("Siedziba", F("AdresPol",
            F("KodKraju", "PL"), F("Wojewodztwo", company.Voivodeship!.Trim()), F("Powiat", company.County!.Trim()), F("Gmina", company.Commune!.Trim()),
            string.IsNullOrWhiteSpace(company.Street) ? null : F("Ulica", company.Street.Trim()),
            F("NrDomu", company.BuildingNumber.Trim()),
            string.IsNullOrWhiteSpace(company.ApartmentNumber) ? null : F("NrLokalu", company.ApartmentNumber!.Trim()),
            F("Miejscowosc", company.City.Trim()), F("KodPocztowy", company.PostalCode.Trim())));
    }

    public static Result Build(Company company, DateOnly from, DateOnly to, DateOnly prepared, StatementValues bs, StatementValues? bsPrev,
        StatementValues pl, StatementValues? plPrev, XsdValidator validator, Extras? extras = null)
    {
        extras ??= new Extras();
        if (extras.Scope != StatementScope.Inna) return BuildSimplified(company, from, to, prepared, bs, bsPrev, pl, plPrev, validator, extras);
        var notImplemented = new List<string>();
        if (string.IsNullOrWhiteSpace(company.PkdCode)) throw new DomainException("Uzupełnij kod PKD przeważającej działalności w danych jednostki (np. 6920Z) — jest wymagany w e-Sprawozdaniu.");
        var siedziba = Siedziba(company);
        XElement T(string n, params object?[] c) => new(Ns + n, c);
        XElement F(string n, params object?[] c) => new(Dtsf + n, c);

        // Wprowadzenie
        var p1b = P1B(Ns, company);
        var policies = Paragraphs(extras.Policies, "Zgodnie z polityką rachunkowości jednostki.").ToList();
        var wprowadzenie = T("WprowadzenieDoSprawozdaniaFinansowego",
            T("P_1",
                T("P_1A", F("NazwaFirmy", company.FullName), siedziba),
                p1b,
                T("P_1C", F("KodPKD", company.PkdCode!.Trim().Replace(".", "").ToUpperInvariant())),
                T("P_1D", Identifiers.NormalizeNip(company.Nip)),
                string.IsNullOrWhiteSpace(company.Krs) ? null : T("P_1E", company.Krs!.Trim())),
            T("P_3", F("DataOd", D(from)), F("DataDo", D(to))),
            T("P_4", "2"),
            T("P_5", T("P_5A", "1"), T("P_5B", extras.GoingConcernThreatened ? "2" : "1"),
                extras.GoingConcernThreatened ? T("P_5C", extras.GoingConcern ?? "") : null),
            T("P_7",
                T("P_7A", policies[0]),
                T("P_7B", policies.Count > 1 ? policies[1] : "Wynik finansowy ustala się w układzie przyjętym w polityce rachunkowości (wariant RZiS: " +
                                                          (extras.CalculationVariant is null ? "porównawczy" : "kalkulacyjny") + ")."),
                T("P_7C", policies.Count > 2 ? string.Join(" ", policies.Skip(2)) : "Sprawozdanie sporządzono zgodnie z załącznikiem nr 1 do ustawy o rachunkowości.")),
            T("P_8", M(extras.AverageEmployment)),
            T("P_9", extras.AuditRequired ? "1" : "2"));
        if (!string.IsNullOrWhiteSpace(extras.GoingConcern))
            wprowadzenie.Add(T("P_10", F("NazwaPozycji", "Założenie kontynuacji działalności"), F("Opis", extras.GoingConcern)));

        // Bilans
        var aktywa = new XElement(Jin + "Aktywa", Amounts(bs.Get("AKTYWA"), bsPrev?.Get("AKTYWA") ?? 0));
        foreach (var l in FinancialStatementLayout.Assets) aktywa.Add(Tree(l, x => "Aktywa_" + ElementSuffix(x.Code), bs, bsPrev));
        var pasywa = new XElement(Jin + "Pasywa", Amounts(bs.Get("PASYWA"), bsPrev?.Get("PASYWA") ?? 0));
        foreach (var l in FinancialStatementLayout.Liabilities) pasywa.Add(Tree(l, x => "Pasywa_" + ElementSuffix(x.Code), bs, bsPrev));

        // RZiS
        XElement rzisVariant;
        if (extras.CalculationVariant is { } calc)
        {
            rzisVariant = new XElement(Jin + "RZiSKalk");
            foreach (var l in FinancialStatementLayout.ProfitLossCalculation) rzisVariant.Add(Tree(l, x => ElementSuffix(x.Code), calc, extras.CalculationVariantPrev));
        }
        else
        {
            rzisVariant = new XElement(Jin + "RZiSPor");
            foreach (var l in FinancialStatementLayout.ProfitLoss) rzisVariant.Add(Tree(l, x => ElementSuffix(x.Code), pl, plPrev));
        }

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "Dokument",
                new XAttribute(XNamespace.Xmlns + "str", Str.NamespaceName), new XAttribute(XNamespace.Xmlns + "dtsf", Dtsf.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "jin", Jin.NamespaceName),
                T("OpisDokumentu"),
                T("DaneDokumentu"),
                T("TrescDokumentu", new XAttribute("format", "text/xml"), new XAttribute("kodowanie", "XML"),
                    T("JednostkaInna",
                        T("Naglowek", F("OkresOd", D(from)), F("OkresDo", D(to)), F("DataSporzadzenia", D(prepared)),
                            new XElement(Jin + "KodSprawozdania", new XAttribute("kodSystemowy", "SFJINZ (2)"), new XAttribute("wersjaSchemy", "1-0E"), "SprFinJednostkaInnaWZlotych"),
                            new XElement(Jin + "WariantSprawozdania", "2")),
                        wprowadzenie,
                        T("Bilans", aktywa, pasywa),
                        T("RZiS", rzisVariant),
                        Equity(Ns + "ZestZmianWKapitale", extras.Equity, extras.EquityPrev, notImplemented),
                        CashFlow(Ns + "RachPrzeplywow", extras.CashFlow, extras.CashFlowPrev, notImplemented),
                        Notes(Ns + "DodatkoweInformacjeIObjasnieniaJednostkaInna", Ns + "DodatkoweInformacjeIObjasnienia", extras, notImplemented)))));
        var validation = validator.Validate(doc, SchemaFile, "e-Sprawozdanie SFJINZ(2)");
        notImplemented.Add("Podpisy kierownika jednostki i osoby sporządzającej (podpis kwalifikowany / profil zaufany / e-dowód) — w aplikacji e-Sprawozdania MF lub e-KRS");
        return new Result(doc, validation, notImplemented);
    }

    private static XElement? Equity(XName container, EquityChanges? eq, EquityChanges? prev, List<string> notImplemented)
    {
        if (eq is null)
        {
            notImplemented.Add("Zestawienie zmian w kapitale (funduszu) własnym — pominięte (jednostka mała/mikro może je pominąć)");
            return null;
        }
        XElement J(string n, decimal a, decimal b, params object?[] children) => new(Jin + n, Amounts(a, b), children);
        EquityChangeRow? R(EquityChanges? e, string code) => e?.Rows.FirstOrDefault(r => r.Code == code);
        XElement Capital(string n, string code)
        {
            var c = R(eq, code);
            var p = R(prev, code);
            return J(n, c?.Opening ?? 0, p?.Opening ?? 0,
                J(n + "_1", (c?.Increase ?? 0) - (c?.Decrease ?? 0), (p?.Increase ?? 0) - (p?.Decrease ?? 0),
                    J(n + "_1_A", c?.Increase ?? 0, p?.Increase ?? 0),
                    J(n + "_1_B", c?.Decrease ?? 0, p?.Decrease ?? 0)),
                J(n + "_2", c?.Closing ?? 0, p?.Closing ?? 0));
        }
        var v = R(eq, "P.A.V");
        var vp = R(prev, "P.A.V");
        var vi = R(eq, "P.A.VI");
        var vip = R(prev, "P.A.VI");
        var vii = R(eq, "P.A.VII");
        var viip = R(prev, "P.A.VII");
        decimal Pos(decimal x) => Math.Max(0, x);
        decimal Neg(decimal x) => Math.Max(0, -x);
        return new XElement(container,
            J("I", eq.Opening, prev?.Opening ?? 0),
            J("IA", eq.Opening, prev?.Opening ?? 0,
                Capital("IA_1", "P.A.I"),
                Capital("IA_2", "P.A.II"),
                Capital("IA_3", "P.A.III"),
                Capital("IA_4", "P.A.IV"),
                J("IA_5", v?.Opening ?? 0, vp?.Opening ?? 0,
                    J("IA_5_7", v?.Closing ?? 0, vp?.Closing ?? 0)),
                J("IA_6", (vi?.Closing ?? 0) + (vii?.Closing ?? 0), (vip?.Closing ?? 0) + (viip?.Closing ?? 0),
                    J("IA_6_A", Pos(vi?.Closing ?? 0), Pos(vip?.Closing ?? 0)),
                    J("IA_6_B", Neg(vi?.Closing ?? 0), Neg(vip?.Closing ?? 0)),
                    J("IA_6_C", -(vii?.Closing ?? 0), -(viip?.Closing ?? 0)))),
            J("II", eq.Closing, prev?.Closing ?? 0),
            J("III", eq.Closing, prev?.Closing ?? 0));
    }

    private static XElement? CashFlow(XName container, CashFlowStatement? cf, CashFlowStatement? prev, List<string> notImplemented)
    {
        if (cf is null)
        {
            notImplemented.Add("Rachunek przepływów pieniężnych — pominięty (wymagany dla jednostek badanych, art. 48a i 64 UoR)");
            return null;
        }
        XElement J(string n, decimal a, decimal b, params object?[] children) => new(Jin + n, Amounts(a, b), children);
        decimal G(CashFlowStatement? s, string code) => s?.Get(code) ?? 0;
        decimal In(CashFlowStatement? s, string code) => Math.Max(0, G(s, code));
        decimal Out(CashFlowStatement? s, string code) => Math.Max(0, -G(s, code));
        var ops = new XElement(Jin + "A_II", Amounts(G(cf, "A.II"), G(prev, "A.II")));
        for (var i = 1; i <= 9; i++) ops.Add(J($"A_II_{i}", G(cf, $"A.II.{i}"), G(prev, $"A.II.{i}")));
        // działalność inwestycyjna i finansowa: pozycje „per saldo” rozdzielone na wpływy i wydatki
        decimal InvIn(CashFlowStatement? s) => In(s, "B.I") + In(s, "B.II") + In(s, "B.III");
        decimal InvOut(CashFlowStatement? s) => Out(s, "B.I") + Out(s, "B.II") + Out(s, "B.III");
        decimal FinIn(CashFlowStatement? s) => In(s, "C.I") + In(s, "C.II");
        decimal FinOut(CashFlowStatement? s) => Out(s, "C.I") + Out(s, "C.II") + Out(s, "C.III");
        return new XElement(container, new XElement(Jin + "PrzeplywyPosr",
            new XElement(Jin + "A",
                J("A_I", G(cf, "A.I"), G(prev, "A.I")),
                ops,
                J("A_III", G(cf, "A.III"), G(prev, "A.III"))),
            new XElement(Jin + "B",
                J("B_I", InvIn(cf), InvIn(prev),
                    J("B_I_1", In(cf, "B.I"), In(prev, "B.I")),
                    J("B_I_4", In(cf, "B.II") + In(cf, "B.III"), In(prev, "B.II") + In(prev, "B.III"))),
                J("B_II", InvOut(cf), InvOut(prev),
                    J("B_II_1", Out(cf, "B.I"), Out(prev, "B.I")),
                    J("B_II_4", Out(cf, "B.II") + Out(cf, "B.III"), Out(prev, "B.II") + Out(prev, "B.III"))),
                J("B_III", G(cf, "B.IV"), G(prev, "B.IV"))),
            new XElement(Jin + "C",
                J("C_I", FinIn(cf), FinIn(prev),
                    J("C_I_1", In(cf, "C.II"), In(prev, "C.II")),
                    J("C_I_2", In(cf, "C.I"), In(prev, "C.I"))),
                J("C_II", FinOut(cf), FinOut(prev),
                    J("C_II_2", Out(cf, "C.II"), Out(prev, "C.II")),
                    J("C_II_4", Out(cf, "C.I"), Out(prev, "C.I")),
                    J("C_II_8", Out(cf, "C.III"), Out(prev, "C.III"))),
                J("C_III", G(cf, "C.IV"), G(prev, "C.IV"))),
            J("D", G(cf, "D"), G(prev, "D")),
            J("E", G(cf, "E"), G(prev, "E")),
            J("F", G(cf, "F"), G(prev, "F")),
            J("G", G(cf, "G"), G(prev, "G"))));
    }

    private static XElement Notes(XName container, XName item, Extras extras, List<string> notImplemented)
    {
        XElement F(string n, params object?[] c) => new(Dtsf + n, c);
        var info = new XElement(container);
        if (extras.NotesPdf is { Length: > 0 } pdf)
            info.Add(new XElement(item, F("Opis", "Informacja dodatkowa (dodatkowe informacje i objaśnienia)"),
                F("Plik", F("Nazwa", extras.NotesFileName), F("Zawartosc", Convert.ToBase64String(pdf)))));
        else
        {
            info.Add(new XElement(item, F("Opis", "Informacja dodatkowa — do uzupełnienia")));
            notImplemented.Add("Dodatkowe informacje i objaśnienia — nie dołączono pliku");
        }
        if (extras.IncomeTax is { } t)
        {
            var p = extras.IncomeTaxPrev;
            XElement Okres(string n, decimal a, decimal? b) => F(n, F("RB", M(a)), b is { } bv ? F("RP", M(bv)) : null);
            XElement Poz(string n, decimal a, decimal? b) => F(n, F("Kwota", F("RB", F("KwotaA", M(a))), b is { } bv ? F("RP", F("KwotaA", M(bv))) : null));
            info.Add(new XElement(container.Namespace + "InformacjaDodatkowaDotyczacaPodatkuDochodowego",
                Okres("P_ID_1", t.ProfitBeforeTax, p?.ProfitBeforeTax),
                Poz("P_ID_2", t.ExemptRevenue, p?.ExemptRevenue),
                Poz("P_ID_3", t.NonTaxableRevenue, p?.NonTaxableRevenue),
                Poz("P_ID_4", t.TaxableRevenuePriorBooks, p?.TaxableRevenuePriorBooks),
                Poz("P_ID_5", t.NonDeductibleCosts, p?.NonDeductibleCosts),
                Poz("P_ID_6", t.NotYetDeductibleCosts, p?.NotYetDeductibleCosts),
                Poz("P_ID_7", t.DeductibleCostsPriorBooks, p?.DeductibleCostsPriorBooks),
                Poz("P_ID_8", t.LossesDeducted, p?.LossesDeducted),
                Poz("P_ID_9", t.OtherChanges, p?.OtherChanges),
                Okres("P_ID_10", t.TaxBase, p?.TaxBase),
                Okres("P_ID_11", t.Tax, p?.Tax)));
        }
        return info;
    }
}
