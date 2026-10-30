using System.Globalization;
using System.Xml.Linq;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Infrastructure.Declarations;

/// <summary>Wynik budowy pliku deklaracji: dokument, wynik walidacji ze schematem i uwagi.</summary>
public sealed record DeclarationXmlResult(XDocument Document, XsdValidationResult Validation, IReadOnlyList<string> Warnings)
{
    public string Status => Validation.IsValid ? "ZWALIDOWANY ze schematem XSD" : "WYGENEROWANY — NIEZWALIDOWANY";
}

/// <summary>Dane podatnika-osoby fizycznej (Podmiot1 / OsobaFizyczna w e-Deklaracjach).</summary>
public sealed record TaxpayerPerson(string FirstName, string LastName, DateOnly BirthDate);

/// <summary>
/// VAT-UE(5) — informacja podsumowująca (CRWDE 2021/01/12/10293, wersja schematu 2-0E), składana za miesiąc wyłącznie elektronicznie.
/// Plik wysyła użytkownik (e-Deklaracje / e-Urząd Skarbowy) i podpisuje go przy wysyłce.
/// </summary>
public static class VatUeXmlBuilder
{
    public static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2021/01/12/10293/";
    public static readonly XNamespace Etd = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2020/03/11/eD/DefinicjeTypy/";
    public const string SchemaFile = "Schemat_VAT-UE(5)_v2-0E.xsd";
    public const string SchemaFileCorrection = "Schemat_VAT-UEK(5)_v2-1E.xsd";

    internal static XElement Podmiot(XNamespace ns, XNamespace etd, Company company, TaxpayerPerson? person)
    {
        var nip = Identifiers.NormalizeNip(company.Nip);
        if (company.LegalForm == LegalForm.DzialalnoscGospodarczaOsobyFizycznej && person is null)
            throw new DomainException("Dla osoby fizycznej deklaracja wymaga imienia, nazwiska i daty urodzenia — dodaj właściciela z numerem PESEL w danych jednostki.");
        return new XElement(ns + "Podmiot1", new XAttribute("rola", "Podatnik"),
            person is not null
                ? new XElement(etd + "OsobaFizyczna", new XElement(etd + "NIP", nip), new XElement(etd + "ImiePierwsze", person.FirstName),
                    new XElement(etd + "Nazwisko", person.LastName), new XElement(etd + "DataUrodzenia", person.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                : new XElement(etd + "OsobaNiefizyczna", new XElement(etd + "NIP", nip), new XElement(etd + "PelnaNazwa", company.FullName)));
    }

    public static DeclarationXmlResult Build(Company company, DateOnly month, IReadOnlyList<ReportService.VatUeLine> lines, XsdValidator validator, TaxpayerPerson? person = null)
    {
        if (string.IsNullOrWhiteSpace(company.TaxOfficeCode)) throw new DomainException("Uzupełnij kod urzędu skarbowego w danych jednostki.");
        var warnings = new List<string>();
        var missing = lines.Where(l => string.IsNullOrWhiteSpace(l.VatNumber)).ToList();
        if (missing.Count > 0)
            throw new DomainException("Brak numeru VAT UE kontrahenta: " + string.Join(", ", missing.Select(m => $"{m.Counterparty} ({m.Country})")) + " — uzupełnij w kartotece kontrahentów.");
        if (lines.Count == 0) warnings.Add("Brak transakcji wewnątrzwspólnotowych w miesiącu — informacji VAT-UE nie składa się.");
        XElement T(string n, params object?[] c) => new(Ns + n, c);
        string Country(string c) => c == "GR" ? "EL" : c;
        var pos = T("PozycjeSzczegolowe",
            lines.Where(l => l.Part == "C").Select(l => T("Grupa1", T("P_Da", Country(l.Country)), T("P_Db", l.VatNumber), T("P_Dc", l.Value.ToString("0", CultureInfo.InvariantCulture)), T("P_Dd", "1"))),
            lines.Where(l => l.Part == "D").Select(l => T("Grupa2", T("P_Na", Country(l.Country)), T("P_Nb", l.VatNumber), T("P_Nc", l.Value.ToString("0", CultureInfo.InvariantCulture)), T("P_Nd", "1"))),
            lines.Where(l => l.Part == "E").Select(l => T("Grupa3", T("P_Ua", Country(l.Country)), T("P_Ub", l.VatNumber), T("P_Uc", l.Value.ToString("0", CultureInfo.InvariantCulture)))));
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "Deklaracja",
                new XAttribute(XNamespace.Xmlns + "etd", Etd.NamespaceName),
                T("Naglowek",
                    T("KodFormularza", new XAttribute("kodSystemowy", "VAT-UE (5)"), new XAttribute("wersjaSchemy", "2-0E"), "VAT-UE"),
                    T("WariantFormularza", "5"),
                    T("Rok", month.Year),
                    T("Miesiac", month.Month),
                    T("CelZlozenia", "1"),
                    T("KodUrzedu", company.TaxOfficeCode)),
                Podmiot(Ns, Etd, company, person),
                pos,
                T("Pouczenie", "1")));
        warnings.Add("Transakcje trójstronne i przemieszczenia call-off stock (część F) — jeśli występują, popraw plik przed wysyłką. Korektę informacji składa się w formularzu VAT-UEK.");
        return new DeclarationXmlResult(doc, validator.Validate(doc, SchemaFile, "VAT-UE(5)"), warnings);
    }

    public static readonly XNamespace NsCorrection = "http://crd.gov.pl/wzor/2021/01/26/10316/";

    /// <summary>
    /// VAT-UEK(5) — korekta informacji podsumowującej (CRWDE 2021/01/26/10316, wersja 2-1E): dla każdej zmienionej pozycji
    /// część „BYŁO” (dane z pierwotnej informacji) i „JEST” (dane po korekcie); pozycja usunięta — tylko „BYŁO”, nowa — tylko „JEST”.
    /// </summary>
    public static DeclarationXmlResult BuildCorrection(Company company, DateOnly month, IReadOnlyList<ReportService.VatUeLine> filed,
        IReadOnlyList<ReportService.VatUeLine> current, XsdValidator validator, TaxpayerPerson? person = null)
    {
        if (string.IsNullOrWhiteSpace(company.TaxOfficeCode)) throw new DomainException("Uzupełnij kod urzędu skarbowego w danych jednostki.");
        var missing = current.Where(l => string.IsNullOrWhiteSpace(l.VatNumber)).ToList();
        if (missing.Count > 0) throw new DomainException("Brak numeru VAT UE kontrahenta: " + string.Join(", ", missing.Select(m => $"{m.Counterparty} ({m.Country})")));
        static (string, string, string) Key(ReportService.VatUeLine l) => (l.Part, l.Country, l.VatNumber);
        var was = filed.ToDictionary(Key);
        var now = current.ToDictionary(Key);
        var changes = was.Keys.Union(now.Keys)
            .Select(k => (k, b: was.GetValueOrDefault(k), j: now.GetValueOrDefault(k)))
            .Where(x => x.b?.Value != x.j?.Value)
            .OrderBy(x => x.k.Item1).ThenBy(x => x.k.Item2).ThenBy(x => x.k.Item3).ToList();
        if (changes.Count == 0) throw new DomainException("Dane VAT-UE nie zmieniły się od złożenia informacji — korekta nie jest potrzebna.");
        XElement T(string n, params object?[] c) => new(NsCorrection + n, c);
        string Country(string c) => c == "GR" ? "EL" : c;
        string V(decimal v) => v.ToString("0", CultureInfo.InvariantCulture);
        IEnumerable<XElement> Side(string prefix, ReportService.VatUeLine? l, bool triangular) => l is null
            ? Array.Empty<XElement>()
            : new[] { T(prefix + "a", Country(l.Country)), T(prefix + "b", l.VatNumber), T(prefix + "c", V(l.Value)) }
                .Concat(triangular ? new[] { T(prefix + "d", "1") } : Array.Empty<XElement>());
        var pos = T("PozycjeSzczegolowe",
            changes.Where(c => c.k.Item1 == "C").Select(c => T("Grupa1", Side("P_DB", c.b, true), Side("P_DJ", c.j, true))),
            changes.Where(c => c.k.Item1 == "D").Select(c => T("Grupa2", Side("P_NB", c.b, true), Side("P_NJ", c.j, true))),
            changes.Where(c => c.k.Item1 == "E").Select(c => T("Grupa3", Side("P_UB", c.b, false), Side("P_UJ", c.j, false))));
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(NsCorrection + "Deklaracja",
                new XAttribute(XNamespace.Xmlns + "etd", Etd.NamespaceName),
                T("Naglowek",
                    T("KodFormularza", new XAttribute("kodSystemowy", "VATUEK (5)"), new XAttribute("wersjaSchemy", "2-1E"), "VAT-UEK"),
                    T("WariantFormularza", "5"),
                    T("Rok", month.Year),
                    T("Miesiac", month.Month),
                    T("CelZlozenia", "1"),
                    T("KodUrzedu", company.TaxOfficeCode)),
                Podmiot(NsCorrection, Etd, company, person),
                pos,
                T("Pouczenie", "1")));
        var warnings = new List<string> { $"Korekta obejmuje {changes.Count} pozycji (BYŁO/JEST). Składa się ją z wyjaśnieniem przyczyn korekty, jeśli urząd o nie wezwie." };
        return new DeclarationXmlResult(doc, validator.Validate(doc, SchemaFileCorrection, "VAT-UEK(5)"), warnings);
    }
}
