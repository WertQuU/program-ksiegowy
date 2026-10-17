using System.Xml.Linq;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Reporting;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Infrastructure.Xml;

/// <summary>
/// Sprawozdanie finansowe w postaci elektronicznej (art. 45 ust. 1g UoR) — struktura „JednostkaInnaWZlotych”.
/// Zaimplementowano: nagłówek, dane identyfikacyjne, bilans, RZiS (wariant porównawczy). NIE zaimplementowano:
/// informacji dodatkowej, zestawienia zmian w kapitale, rachunku przepływów pieniężnych (wymagają danych spoza ksiąg).
/// Nazwy elementów odtworzone według wzorca struktur MF — WYMAGAJĄ walidacji z oficjalnym XSD; bez niej plik ma status NIEZWALIDOWANY.
/// </summary>
public static class FinancialStatementXmlBuilder
{
    public static readonly XNamespace Ns = "http://www.mf.gov.pl/schematy/SF/DefinicjeTypySprawozdaniaFinansowe/2018/07/09/JednostkaInnaWZlotych";
    public static readonly XNamespace Dtsf = "http://www.mf.gov.pl/schematy/SF/DefinicjeTypySprawozdaniaFinansowe/2018/07/09/DefinicjeTypySprawozdaniaFinansowe/";
    public const string SchemaFile = "JednostkaInnaWZlotych(1)_v1-3.xsd";

    public sealed record Result(XDocument Document, XsdValidationResult Validation, IReadOnlyList<string> NotImplemented)
    {
        public string Status => Validation.IsValid ? "ZWALIDOWANY ze schematem XSD" : "WYGENEROWANY — NIEZWALIDOWANY";
    }

    public static Result Build(Company company, DateOnly from, DateOnly to, DateOnly prepared, StatementValues bs, StatementValues? bsPrev,
        StatementValues pl, StatementValues? plPrev, XsdValidator validator)
    {
        XElement Amounts(string name, decimal a, decimal b) => new(Ns + name, new XElement(Dtsf + "KwotaA", Money.Xml(a)), new XElement(Dtsf + "KwotaB", Money.Xml(b)));

        XElement Tree(string prefix, StatementLine line, StatementValues cur, StatementValues? prev)
        {
            var name = prefix + "_" + ElementSuffix(line.Code);
            var e = Amounts(name, cur.Get(line.Code), prev?.Get(line.Code) ?? 0);
            foreach (var c in line.Children) e.Add(Tree(prefix, c, cur, prev));
            return e;
        }

        var aktywa = Amounts("Aktywa", bs.Get("AKTYWA"), bsPrev?.Get("AKTYWA") ?? 0);
        foreach (var l in FinancialStatementLayout.Assets) aktywa.Add(Tree("Aktywa", l, bs, bsPrev));
        var pasywa = Amounts("Pasywa", bs.Get("PASYWA"), bsPrev?.Get("PASYWA") ?? 0);
        foreach (var l in FinancialStatementLayout.Liabilities) pasywa.Add(Tree("Pasywa", l, bs, bsPrev));
        var rzis = new XElement(Ns + "RZiSPor");
        foreach (var l in FinancialStatementLayout.ProfitLoss) rzis.Add(Tree("RZiSPor", l, pl, plPrev));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "JednostkaInna", new XAttribute(XNamespace.Xmlns + "dtsf", Dtsf.NamespaceName),
                new XElement(Ns + "Naglowek",
                    new XElement(Dtsf + "OkresOd", from.ToString("yyyy-MM-dd")),
                    new XElement(Dtsf + "OkresDo", to.ToString("yyyy-MM-dd")),
                    new XElement(Dtsf + "DataSporzadzenia", prepared.ToString("yyyy-MM-dd"))),
                new XElement(Ns + "WprowadzenieDoSprawozdaniaFinansowego",
                    new XElement(Ns + "P_1",
                        new XElement(Ns + "P_1A", new XElement(Dtsf + "NazwaFirmy", company.FullName), new XElement(Dtsf + "Siedziba", new XElement(Dtsf + "KodKraju", "PL"), new XElement(Dtsf + "Miejscowosc", company.City))),
                        new XElement(Ns + "P_1B", new XElement(Dtsf + "Adres", new XElement(Dtsf + "KodKraju", "PL"), new XElement(Dtsf + "Ulica", company.Street),
                            new XElement(Dtsf + "NrDomu", company.BuildingNumber), new XElement(Dtsf + "Miejscowosc", company.City), new XElement(Dtsf + "KodPocztowy", company.PostalCode))),
                        new XElement(Ns + "P_1C", new XElement(Dtsf + "KRS", company.Krs ?? "")),
                        new XElement(Ns + "P_1D", Identifiers.NormalizeNip(company.Nip))),
                    new XElement(Ns + "P_3", new XElement(Dtsf + "DataOd", from.ToString("yyyy-MM-dd")), new XElement(Dtsf + "DataDo", to.ToString("yyyy-MM-dd")))),
                new XElement(Ns + "Bilans", aktywa, pasywa),
                rzis));
        var validation = validator.Validate(doc, SchemaFile, "e-Sprawozdanie JednostkaInnaWZlotych");
        return new Result(doc, validation, new[]
        {
            "Wprowadzenie do sprawozdania — pozycje P_2, P_4–P_8 (m.in. założenie kontynuacji działalności, zasady rachunkowości) — do uzupełnienia",
            "Dodatkowe informacje i objaśnienia (informacja dodatkowa)",
            "Zestawienie zmian w kapitale (funduszu) własnym",
            "Rachunek przepływów pieniężnych (dla jednostek objętych obowiązkiem badania)",
            "Podpisy kierownika jednostki i osoby sporządzającej (kwalifikowany podpis / profil zaufany) — poza programem"
        });
    }

    /// <summary>„A.II.1.a” → „A_II_1_A” (wzorzec nazw elementów struktur MF; do potwierdzenia XSD).</summary>
    public static string ElementSuffix(string code) =>
        string.Join("_", (code.StartsWith("P.") || code.StartsWith("R.") ? code[2..] : code).Split('.').Select(s => s.Length == 1 && char.IsLower(s[0]) ? s.ToUpperInvariant() : s));
}
