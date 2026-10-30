using System.Globalization;
using System.Xml.Linq;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Infrastructure.Declarations;

/// <summary>
/// PIT-11(29) — informacja o dochodach oraz pobranych zaliczkach (CRWDE 2022/11/09/11890, aktualny wzór według podatki.gov.pl).
/// Część E: wiersz 1 — stosunek pracy (poz. 29–35), wiersz 6 — działalność wykonywana osobiście, w tym zlecenie, dzieło
/// i powołanie (poz. 58–61), wiersz 9 — prawa autorskie (poz. 70–74); poz. 95 — składki społeczne, poz. 109–111 — przychody
/// zwolnione (ulga dla młodych), poz. 122 — składka zdrowotna.
/// </summary>
public static class Pit11XmlBuilder
{
    public static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2022/11/09/11890/";
    public static readonly XNamespace Etd = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/03/15/eD/DefinicjeTypy/";
    public const string SchemaFile = "Schemat_PIT-11(29)_v1-1E.xsd";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string A(decimal v) => Math.Round(Math.Max(0, v), 2, MidpointRounding.AwayFromZero).ToString("0.00", Inv);
    private static string C(decimal v) => Math.Round(Math.Max(0, v), 0, MidpointRounding.AwayFromZero).ToString("0", Inv);

    private static XElement? Address(ReportService.Pit11Address? a, Func<string, object?[], XElement> t)
    {
        if (a is null) return null;
        XElement T(string n, params object?[] c) => t(n, c);
        XElement? Opt(string n, string? v, string? poz = null) =>
            string.IsNullOrWhiteSpace(v) ? null : T(n, poz is null ? null : new XAttribute("poz", poz), v.Trim());
        return T("AdresZamieszkania", new XAttribute("rodzajAdresu", "RAD"),
            T("KodKraju", new XAttribute("poz", "P_19A"), "PL"),
            Opt("Wojewodztwo", a.Voivodeship), Opt("Powiat", a.County), Opt("Gmina", a.Commune),
            Opt("Ulica", a.Street, "P_23"), Opt("NrDomu", a.HouseNumber, "P_24"), Opt("NrLokalu", a.FlatNumber, "P_25"),
            T("Miejscowosc", new XAttribute("poz", "P_26"), a.City.Trim()), Opt("KodPocztowy", a.PostalCode, "P_27"));
    }

    public static DeclarationXmlResult Build(Company company, int year, ReportService.Pit11Person p, XsdValidator validator, TaxpayerPerson? payerPerson = null)
    {
        var who = $"{p.FirstName} {p.LastName}";
        if (string.IsNullOrWhiteSpace(p.Pesel)) throw new DomainException($"{who}: brak numeru PESEL (wymagany w PIT-11).");
        if (p.BirthDate is null) throw new DomainException($"{who}: brak daty urodzenia.");
        if (string.IsNullOrWhiteSpace(p.TaxOfficeCode)) throw new DomainException($"{who}: brak kodu urzędu skarbowego podatnika (kartoteka pracownika) — PIT-11 kieruje się do urzędu właściwego dla podatnika.");
        var warnings = new List<string>();
        XElement T(string n, params object?[] c) => new(Ns + n, c);
        XElement D(string n, params object?[] c) => new(Etd + n, c);

        var pos = T("PozycjeSzczegolowe", T("P_11", "1"));
        foreach (var s in p.Sources.Where(s => s.Contract == ContractType.UmowaOPrace))
        {
            pos.Add(T("P_29", A(s.Revenue)), T("P_30", A(s.Costs)), T("P_31", A(s.Revenue - s.Costs)), T("P_33", C(s.Advance)));
            if (s.AuthorCosts) pos.Add(T("P_34", A(s.AuthorRevenue)), T("P_35", A(s.AuthorCostsAmount)));
        }
        var civil = p.Sources.Where(s => s.Contract != ContractType.UmowaOPrace).ToList();
        foreach (var s in civil.Where(s => !s.AuthorCosts))
            pos.Add(T("P_58", A(s.Revenue)), T("P_59", A(s.Costs)), T("P_60", A(s.Revenue - s.Costs)), T("P_61", C(s.Advance)));
        foreach (var s in civil.Where(s => s.AuthorCosts))
            pos.Add(T("P_70", A(s.Revenue)), T("P_71", A(s.Revenue - s.Costs)), T("P_72", C(s.Advance)), T("P_73", A(s.AuthorRevenue)), T("P_74", A(s.AuthorCostsAmount)));
        if (p.Social > 0) pos.Add(T("P_95", A(p.Social)));
        if (p.ExemptEmployment + p.ExemptCivil > 0)
            pos.Add(T("P_109", A(p.ExemptEmployment + p.ExemptCivil)),
                p.ExemptEmployment > 0 ? T("P_110", A(p.ExemptEmployment)) : null,
                p.ExemptCivil > 0 ? T("P_111", A(p.ExemptCivil)) : null);
        pos.Add(T("P_121", "2"));
        if (p.Health > 0) pos.Add(T("P_122", A(p.Health)));

        var nip = Identifiers.NormalizeNip(company.Nip);
        var payer = payerPerson is not null
            ? D("OsobaFizyczna", D("NIP", nip), D("ImiePierwsze", payerPerson.FirstName), D("Nazwisko", payerPerson.LastName), D("DataUrodzenia", payerPerson.BirthDate.ToString("yyyy-MM-dd", Inv)))
            : T("OsobaNiefizyczna", T("NIP", nip), T("PelnaNazwa", company.FullName));
        if (company.LegalForm == LegalForm.DzialalnoscGospodarczaOsobyFizycznej && payerPerson is null)
            throw new DomainException("Płatnik jest osobą fizyczną — dodaj właściciela z numerem PESEL w danych jednostki.");
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "Deklaracja",
                new XAttribute(XNamespace.Xmlns + "etd", Etd.NamespaceName),
                T("Naglowek",
                    T("KodFormularza", new XAttribute("kodSystemowy", "PIT-11 (29)"), new XAttribute("kodPodatku", "PIT"), new XAttribute("rodzajZobowiazania", "Z"),
                        new XAttribute("wersjaSchemy", "1-1E"), "PIT-11"),
                    T("WariantFormularza", "29"),
                    T("CelZlozenia", new XAttribute("poz", "P_7"), "1"),
                    T("Rok", year),
                    T("KodUrzedu", p.TaxOfficeCode!.Trim())),
                T("Podmiot1", new XAttribute("rola", "Płatnik/Składający"), payer),
                T("Podmiot2", new XAttribute("rola", "Podatnik"),
                    T("OsobaFizyczna", T("PESEL", p.Pesel!.Trim()), T("ImiePierwsze", p.FirstName), T("Nazwisko", p.LastName),
                        T("DataUrodzenia", p.BirthDate.Value.ToString("yyyy-MM-dd", Inv))),
                    Address(p.Address, T)),
                pos,
                T("Pouczenie", "1")));
        if (p.Address is null) warnings.Add($"{who}: brak adresu zamieszkania w kartotece (poz. 19–27) — uzupełnij w sekcji Płace.");
        warnings.Add($"{who}: sprawdź przypisanie przychodów do wierszy części E przed wysyłką.");
        return new DeclarationXmlResult(doc, validator.Validate(doc, SchemaFile, "PIT-11(29)"), warnings);
    }
}
