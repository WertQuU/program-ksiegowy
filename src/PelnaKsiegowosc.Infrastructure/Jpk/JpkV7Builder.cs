using System.Globalization;
using System.Xml.Linq;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Infrastructure.Jpk;

/// <summary>Wersja struktury JPK_V7 (oficjalne schematy MF w katalogu schemas/MF).</summary>
public sealed record JpkV7Version(string Name, XNamespace Ns, string SystemCode, string DeclarationSystemCode, string DeclarationVariant, string SchemaFile, bool KsefMarkers)
{
    public string Variant => SystemCode[^2..^1];
}

/// <summary>
/// Parametry struktur JPK_V7M/V7K potwierdzone oficjalnymi schematami (crd.gov.pl): wersja (3) — rok od 2026, data wytworzenia
/// od 1.02.2026, oznaczenia KSeF (NrKSeF/OFF/BFK/DI); wersja (2) — wcześniejsze okresy.
/// </summary>
public static class JpkV7Rules
{
    public const string SchemaVersion = "1-0E";
    public static readonly JpkV7Version M3 = new("JPK_V7M(3)", "http://crd.gov.pl/wzor/2025/12/19/14090/", "JPK_V7M (3)", "VAT-7 (23)", "23", "Schemat_JPK_V7M(3)_v1-0E.xsd", true);
    public static readonly JpkV7Version K3 = new("JPK_V7K(3)", "http://crd.gov.pl/wzor/2025/12/19/14089/", "JPK_V7K (3)", "VAT-7K (17)", "17", "Schemat_JPK_V7K(3)_v1-0E.xsd", true);
    public static readonly JpkV7Version M2 = new("JPK_V7M(2)", "http://crd.gov.pl/wzor/2021/12/27/11148/", "JPK_V7M (2)", "VAT-7 (22)", "22", "Schemat_JPK_V7M(2)_v1-0E.xsd", false);
    public static readonly JpkV7Version K2 = new("JPK_V7K(2)", "http://crd.gov.pl/wzor/2021/12/27/11149/", "JPK_V7K (2)", "VAT-7K (16)", "16", "Schemat_JPK_V7K(2)_v1-0E.xsd", false);
    public static readonly XNamespace Ns = M3.Ns;
    public const string SchemaFile = "Schemat_JPK_V7M(3)_v1-0E.xsd";
    public const string SchemaFileQuarterly = "Schemat_JPK_V7K(3)_v1-0E.xsd";

    /// <summary>Pierwszy okres rozliczeniowy, za który składa się JPK_V7 w wersji 3 (luty 2026).</summary>
    public static readonly DateOnly Version3From = new(2026, 2, 1);

    /// <summary>
    /// Wersja struktury zależy od OKRESU rozliczeniowego (także przy korekcie): za okresy do stycznia 2026 — wersja 2,
    /// od lutego 2026 — wersja 3. Data wytworzenia nie decyduje o wersji.
    /// </summary>
    public static JpkV7Version For(bool quarterly, DateOnly period, DateTime generatedUtc)
    {
        var v3 = new DateOnly(period.Year, period.Month, 1) >= Version3From;
        return quarterly ? (v3 ? K3 : K2) : (v3 ? M3 : M2);
    }
}

public sealed record JpkResult(XDocument Document, XsdValidationResult Validation, IReadOnlyList<string> Warnings)
{
    public string Status => Validation.IsValid ? "ZWALIDOWANY ze schematem XSD" : "WYGENEROWANY — NIEZWALIDOWANY (nie składać bez walidacji)";
}

/// <summary>
/// Generator JPK_V7M / JPK_V7K z ewidencji VAT (rejestry) i wyliczonej deklaracji. JPK_V7K: ewidencja za każdy miesiąc,
/// część deklaracyjna (za kwartał) tylko w pliku za ostatni miesiąc kwartału. Przestrzeń nazw pobierana z wczytanego schematu.
/// </summary>
public static class JpkV7Builder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static JpkResult Build(Company company, DateOnly period, IReadOnlyList<VatRegisterEntry> sales, IReadOnlyList<VatRegisterEntry> purchases,
        VatDeclaration declaration, bool correction, DateTime generatedUtc, XsdValidator validator) =>
        Build(company, period, sales, purchases, declaration, declaration, correction, generatedUtc, validator);

    /// <param name="declaration">Deklaracja (miesięczna lub kwartalna); <c>null</c> w 1. i 2. miesiącu kwartału (JPK_V7K bez deklaracji).</param>
    /// <param name="monthControl">Zestawienie z rejestrów samego miesiąca — sumy kontrolne ewidencji.</param>
    /// <summary>Dane podatnika będącego osobą fizyczną (sekcja OsobaFizyczna).</summary>
    public sealed record NaturalPerson(string FirstName, string LastName, DateOnly BirthDate);

    public static JpkResult Build(Company company, DateOnly period, IReadOnlyList<VatRegisterEntry> sales, IReadOnlyList<VatRegisterEntry> purchases,
        VatDeclaration? declaration, VatDeclaration monthControl, bool correction, DateTime generatedUtc, XsdValidator validator, NaturalPerson? owner = null)
    {
        var quarterly = company.VatPeriod == VatSettlementPeriod.Kwartalny;
        var ver = JpkV7Rules.For(quarterly, period, generatedUtc);
        var ns = ver.Ns;
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(company.Email)) throw new DomainException("Uzupełnij adres e-mail w danych jednostki — jest wymagany w JPK (Podmiot1).");
        if (string.IsNullOrWhiteSpace(company.TaxOfficeCode)) throw new DomainException("Uzupełnij kod urzędu skarbowego w danych jednostki (wymagany w nagłówku JPK).");
        if (quarterly && period.Month % 3 == 0 && declaration is null) throw new DomainException("W ostatnim miesiącu kwartału JPK_V7K musi zawierać deklarację za kwartał.");
        if (quarterly && period.Month % 3 != 0) declaration = null;
        if (!quarterly && declaration is null) throw new DomainException("JPK_V7M wymaga części deklaracyjnej.");
        if (company.LegalForm == LegalForm.DzialalnoscGospodarczaOsobyFizycznej && owner is null)
            throw new DomainException("Dla osoby fizycznej JPK wymaga imienia, nazwiska i daty urodzenia — dodaj właściciela z numerem PESEL w danych jednostki (osoby odpowiedzialne).");
        if (declaration is not null) warnings.AddRange(declaration.Warnings);
        if (quarterly && declaration is null) warnings.Add($"JPK_V7K za {period:MM.yyyy}: tylko ewidencja — deklaracja za kwartał w pliku za {period.AddMonths(3 - (period.Month - 1) % 3 - 1):MM.yyyy}.");

        XElement E(string n, object? v) => new(ns + n, v);
        var naglowek = new XElement(ns + "Naglowek",
            new XElement(ns + "KodFormularza", new XAttribute("kodSystemowy", ver.SystemCode), new XAttribute("wersjaSchemy", JpkV7Rules.SchemaVersion), "JPK_VAT"),
            E("WariantFormularza", ver.Variant),
            E("DataWytworzeniaJPK", generatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", Inv)),
            E("NazwaSystemu", "Pełna Księgowość"),
            new XElement(ns + "CelZlozenia", new XAttribute("poz", "P_7"), correction ? "2" : "1"),
            E("KodUrzedu", company.TaxOfficeCode),
            E("Rok", period.Year),
            E("Miesiac", period.Month));
        var podmiot = owner is not null
            ? new XElement(ns + "Podmiot1", new XAttribute("rola", "Podatnik"),
                new XElement(ns + "OsobaFizyczna",
                    E("NIP", Identifiers.NormalizeNip(company.Nip)),
                    E("ImiePierwsze", owner.FirstName),
                    E("Nazwisko", owner.LastName),
                    E("DataUrodzenia", owner.BirthDate.ToString("yyyy-MM-dd", Inv)),
                    E("Email", company.Email ?? ""),
                    string.IsNullOrWhiteSpace(company.Phone) ? null : E("Telefon", company.Phone)))
            : new XElement(ns + "Podmiot1", new XAttribute("rola", "Podatnik"),
            new XElement(ns + "OsobaNiefizyczna",
                E("NIP", Identifiers.NormalizeNip(company.Nip)),
                E("PelnaNazwa", company.FullName),
                E("Email", company.Email ?? ""),
                string.IsNullOrWhiteSpace(company.Phone) ? null : E("Telefon", company.Phone)));
        XElement? deklaracja = null;
        if (declaration is not null)
        {
            var pos = new XElement(ns + "PozycjeSzczegolowe");
            foreach (var (k, v) in declaration.Fields.OrderBy(f => int.Parse(f.Key[2..], Inv)))
                if (v != 0 || k is "P_38" or "P_51") pos.Add(E(k, v.ToString("0", Inv)));
            deklaracja = new XElement(ns + "Deklaracja",
                new XElement(ns + "Naglowek",
                    new XElement(ns + "KodFormularzaDekl", new XAttribute("kodSystemowy", ver.DeclarationSystemCode), new XAttribute("kodPodatku", "VAT"),
                        new XAttribute("rodzajZobowiazania", "Z"), new XAttribute("wersjaSchemy", JpkV7Rules.SchemaVersion), quarterly ? "VAT-7K" : "VAT-7"),
                    E("WariantFormularzaDekl", ver.DeclarationVariant),
                    quarterly ? E("Kwartal", (period.Month - 1) / 3 + 1) : null),
                pos,
                E("Pouczenia", "1"));
        }

        var ewidencja = new XElement(ns + "Ewidencja");
        var lp = 1;
        foreach (var g in sales.GroupBy(s => s.DocumentId))
        {
            var f = g.First();
            var row = new XElement(ns + "SprzedazWiersz", E("LpSprzedazy", lp++));
            if (f.CounterpartyCountry != "PL") row.Add(E("KodKrajuNadaniaTIN", f.CounterpartyCountry));
            row.Add(E("NrKontrahenta", string.IsNullOrWhiteSpace(f.CounterpartyNip) ? "brak" : f.CounterpartyNip),
                E("NazwaKontrahenta", f.CounterpartyName),
                E("DowodSprzedazy", f.DocumentNumber),
                E("DataWystawienia", f.IssueDate.ToString("yyyy-MM-dd", Inv)));
            if (f.OperationDate != f.IssueDate) row.Add(E("DataSprzedazy", f.OperationDate.ToString("yyyy-MM-dd", Inv)));
            if (ver.KsefMarkers) AddKsefMarker(row, ns, f);
            if (f.JpkDocumentType is { Length: > 0 } t) row.Add(E("TypDokumentu", t));
            foreach (var gtu in (f.GtuCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) row.Add(E(gtu, "1"));
            foreach (var proc in VatRules.JpkProcedures(f.Procedures)) row.Add(E(proc, "1"));
            foreach (var fld in g.SelectMany(e => VatRules.NetFields(e.JpkNetField).Select(n => ((string?)n, e.NetPln)).Append((e.JpkTaxField, e.VatPln))).Where(x => x.Item1 is not null)
                         .GroupBy(x => x.Item1!).OrderBy(x => int.Parse(x.Key[2..], Inv)))
                row.Add(E(fld.Key, Money.Xml(fld.Sum(x => x.Item2))));
            ewidencja.Add(row);
        }
        ewidencja.Add(new XElement(ns + "SprzedazCtrl", E("LiczbaWierszySprzedazy", lp - 1), E("PodatekNalezny", Money.Xml(monthControl.SalesTaxControl))));
        lp = 1;
        foreach (var g in purchases.GroupBy(s => s.DocumentId))
        {
            var f = g.First();
            var row = new XElement(ns + "ZakupWiersz", E("LpZakupu", lp++));
            if (f.CounterpartyCountry != "PL") row.Add(E("KodKrajuNadaniaTIN", f.CounterpartyCountry));
            row.Add(E("NrDostawcy", string.IsNullOrWhiteSpace(f.CounterpartyNip) ? "brak" : f.CounterpartyNip),
                E("NazwaDostawcy", f.CounterpartyName),
                E("DowodZakupu", f.DocumentNumber),
                E("DataZakupu", f.IssueDate.ToString("yyyy-MM-dd", Inv)));
            if (f.ReceiptDate is { } rd && rd != f.IssueDate) row.Add(E("DataWplywu", rd.ToString("yyyy-MM-dd", Inv)));
            if (ver.KsefMarkers) AddKsefMarker(row, ns, f);
            if (f.JpkDocumentType is { Length: > 0 } t) row.Add(E("DokumentZakupu", t));
            foreach (var fld in g.SelectMany(e => new[] { (e.JpkNetField, e.NetPln), (e.JpkTaxField, e.DeductibleVatPln) }).Where(x => x.Item1 is not null)
                         .GroupBy(x => x.Item1!).OrderBy(x => int.Parse(x.Key[2..], Inv)))
                row.Add(E(fld.Key, Money.Xml(fld.Sum(x => x.Item2))));
            ewidencja.Add(row);
        }
        ewidencja.Add(new XElement(ns + "ZakupCtrl", E("LiczbaWierszyZakupow", lp - 1), E("PodatekNaliczony", Money.Xml(monthControl.PurchaseTaxControl))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "JPK", new XAttribute(XNamespace.Xmlns + "tns", ns.NamespaceName), naglowek, podmiot, deklaracja, ewidencja));
        var validation = validator.Validate(doc, ver.SchemaFile, ver.Name);
        if (!validation.IsValid) warnings.Add("Plik JPK nie został zweryfikowany oficjalnym schematem XSD — nie wysyłaj go bez walidacji.");
        return new JpkResult(doc, validation, warnings);
    }

    private static void AddKsefMarker(XElement row, XNamespace ns, VatRegisterEntry e)
    {
        switch (e.KsefMarker)
        {
            case JpkKsefMarker.NrKSeF when !string.IsNullOrWhiteSpace(e.KsefNumber): row.Add(new XElement(ns + "NrKSeF", e.KsefNumber)); break;
            case JpkKsefMarker.OFF: row.Add(new XElement(ns + "OFF", "1")); break;
            case JpkKsefMarker.DI: row.Add(new XElement(ns + "DI", "1")); break;
            default: row.Add(new XElement(ns + "BFK", "1")); break;
        }
    }
}
