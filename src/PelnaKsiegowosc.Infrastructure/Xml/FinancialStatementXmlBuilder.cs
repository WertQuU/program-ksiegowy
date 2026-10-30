using System.Xml.Linq;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Reporting;
using PelnaKsiegowosc.Domain.Validation;

namespace PelnaKsiegowosc.Infrastructure.Xml;

/// <summary>
/// Sprawozdanie finansowe w postaci elektronicznej (art. 45 ust. 1g UoR) — struktura „JednostkaInnaWZlotych”.
/// Zawiera: nagłówek, dane identyfikacyjne, wprowadzenie (kontynuacja działalności, zasady), bilans, RZiS (wariant
/// porównawczy lub kalkulacyjny), zestawienie zmian w kapitale, rachunek przepływów (metoda pośrednia) oraz informację
/// dodatkową dołączoną jako plik PDF. Nazwy elementów odtworzone według wzorca struktur MF — WYMAGAJĄ walidacji z oficjalnym XSD; bez niej plik ma status NIEZWALIDOWANY.
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

    /// <summary>Części sprawozdania dodatkowe względem bilansu i RZiS porównawczego.</summary>
    public sealed record Extras(
        StatementValues? CalculationVariant = null, StatementValues? CalculationVariantPrev = null,
        EquityChanges? Equity = null, EquityChanges? EquityPrev = null,
        CashFlowStatement? CashFlow = null, CashFlowStatement? CashFlowPrev = null,
        string? GoingConcern = null, string? Policies = null,
        byte[]? NotesPdf = null, string NotesFileName = "informacja-dodatkowa.pdf");

    public static Result Build(Company company, DateOnly from, DateOnly to, DateOnly prepared, StatementValues bs, StatementValues? bsPrev,
        StatementValues pl, StatementValues? plPrev, XsdValidator validator, Extras? extras = null)
    {
        extras ??= new Extras();
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
        XElement rzisVariant;
        if (extras.CalculationVariant is { } calc)
        {
            rzisVariant = new XElement(Ns + "RZiSKalk");
            foreach (var l in FinancialStatementLayout.ProfitLossCalculation) rzisVariant.Add(Tree("RZiSKalk", l, calc, extras.CalculationVariantPrev));
        }
        else
        {
            rzisVariant = new XElement(Ns + "RZiSPor");
            foreach (var l in FinancialStatementLayout.ProfitLoss) rzisVariant.Add(Tree("RZiSPor", l, pl, plPrev));
        }
        var rzis = new XElement(Ns + "RZiS", rzisVariant);
        var notImplemented = new List<string>();

        XElement? equity = null;
        if (extras.Equity is { } eq)
        {
            equity = new XElement(Ns + "ZestZmianWKapitale",
                Amounts("KapitalWlasnyNaPoczatekOkresu", eq.Opening, extras.EquityPrev?.Opening ?? 0));
            foreach (var row in eq.Rows)
            {
                var prevRow = extras.EquityPrev?.Rows.FirstOrDefault(p => p.Code == row.Code);
                equity.Add(new XElement(Ns + "Pozycja_" + ElementSuffix(row.Code),
                    Amounts("NaPoczatek", row.Opening, prevRow?.Opening ?? 0),
                    Amounts("Zwiekszenia", row.Increase, prevRow?.Increase ?? 0),
                    Amounts("Zmniejszenia", row.Decrease, prevRow?.Decrease ?? 0),
                    Amounts("NaKoniec", row.Closing, prevRow?.Closing ?? 0)));
            }
            equity.Add(Amounts("KapitalWlasnyNaKoniecOkresu", eq.Closing, extras.EquityPrev?.Closing ?? 0));
        }
        else notImplemented.Add("Zestawienie zmian w kapitale (funduszu) własnym — nie dołączono (jednostka mikro/mała może je pominąć)");

        XElement? cashFlow = null;
        if (extras.CashFlow is { } cf)
        {
            var posr = new XElement(Ns + "PrzeplywyPosr");
            foreach (var l in cf.Lines) posr.Add(Amounts("Poz_" + l.Code.Replace('.', '_'), l.Amount, extras.CashFlowPrev?.Get(l.Code) ?? 0));
            cashFlow = new XElement(Ns + "RachPrzeplywow", posr);
        }
        else notImplemented.Add("Rachunek przepływów pieniężnych — nie dołączono (wymagany dla jednostek badanych, art. 48a UoR)");

        XElement? notes = null;
        if (extras.NotesPdf is { Length: > 0 } pdf)
            notes = new XElement(Ns + "DodatkoweInformacjeIObjasnieniaJednostkaInna",
                new XElement(Ns + "DodatkoweInformacjeIObjasnienia",
                    new XElement(Dtsf + "Opis", "Informacja dodatkowa"),
                    new XElement(Dtsf + "Plik", new XElement(Dtsf + "Nazwa", extras.NotesFileName), new XElement(Dtsf + "Zawartosc", Convert.ToBase64String(pdf)))));
        else notImplemented.Add("Dodatkowe informacje i objaśnienia — nie dołączono pliku");

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
                    new XElement(Ns + "P_3", new XElement(Dtsf + "DataOd", from.ToString("yyyy-MM-dd")), new XElement(Dtsf + "DataDo", to.ToString("yyyy-MM-dd"))),
                    extras.GoingConcern is null ? null : new XElement(Ns + "P_5", new XElement(Dtsf + "P_5A", true), new XElement(Dtsf + "P_5B", extras.GoingConcern)),
                    extras.Policies is null ? null : new XElement(Ns + "P_7", new XElement(Dtsf + "P_7A", extras.Policies))),
                new XElement(Ns + "Bilans", aktywa, pasywa),
                rzis, equity, cashFlow, notes));
        var validation = validator.Validate(doc, SchemaFile, "e-Sprawozdanie JednostkaInnaWZlotych");
        if (extras.GoingConcern is null || extras.Policies is null) notImplemented.Add("Wprowadzenie — kontynuacja działalności / zasady rachunkowości (uzupełnij teksty informacji dodatkowej)");
        notImplemented.Add("Podpisy kierownika jednostki i osoby sporządzającej (podpis kwalifikowany / profil zaufany / e-dowód) — w aplikacji e-Sprawozdania MF");
        return new Result(doc, validation, notImplemented);
    }

    /// <summary>„A.II.1.a” → „A_II_1_A” (wzorzec nazw elementów struktur MF; do potwierdzenia XSD).</summary>
    public static string ElementSuffix(string code) =>
        string.Join("_", (code.StartsWith("P.") || code.StartsWith("R.") || code.StartsWith("K.") ? code[2..] : code).Split('.').Select(s => s.Length == 1 && char.IsLower(s[0]) ? s.ToUpperInvariant() : s));
}
