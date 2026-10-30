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
/// VAT-UE(5) — informacja podsumowująca (CRWDE 2020/07/03/9690), składana za miesiąc wyłącznie elektronicznie.
/// Plik wysyła użytkownik (e-Deklaracje / e-Urząd Skarbowy) i podpisuje go przy wysyłce.
/// </summary>
public static class VatUeXmlBuilder
{
    public static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2020/07/03/9690/";
    public static readonly XNamespace Etd = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2020/03/11/eD/DefinicjeTypy/";
    public const string SchemaFile = "Schemat_VAT-UE(5)_v1-0E.xsd";

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
                    T("KodFormularza", new XAttribute("kodSystemowy", "VAT-UE (5)"), new XAttribute("wersjaSchemy", "1-0E"), "VAT-UE"),
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
}
