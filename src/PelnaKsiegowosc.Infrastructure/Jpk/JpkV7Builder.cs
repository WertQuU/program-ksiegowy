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

/// <summary>
/// Parametry struktury JPK_V7M(3). Namespace i oznaczenia nagłówka pochodzą ze źródeł pośrednich (CRWDE 2025/12/19/14090) —
/// WYMAGAJĄ potwierdzenia z oficjalnym schematem; plik nie jest oznaczany jako poprawny bez walidacji XSD.
/// </summary>
public static class JpkV7Rules
{
    public const string Version = "JPK_V7M(3) — odwzorowanie wstępne 2026.02";
    public static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2025/12/19/14090/";
    public const string SystemCode = "JPK_V7M (3)";
    public const string SchemaVersion = "1-0E";
    public const string Variant = "3";
    /// <summary>Wariant części deklaracyjnej VAT-7 — NIEPOTWIERDZONY dla struktury (3).</summary>
    public const string DeclarationSystemCode = "VAT-7 (23)";
    public const string DeclarationVariant = "23";
    public const string SchemaFile = "Schemat_JPK_V7M(3)_v1-0E.xsd";
    public const string SchemaFileQuarterly = "Schemat_JPK_V7K(3)_v1-0E.xsd";
    public const string QuarterlySystemCode = "JPK_V7K (3)";
    /// <summary>Wariant części deklaracyjnej VAT-7K — NIEPOTWIERDZONY dla struktury (3).</summary>
    public const string QuarterlyDeclarationSystemCode = "VAT-7K (17)";
    public const string QuarterlyDeclarationVariant = "17";
    /// <summary>Zastępcza przestrzeń nazw JPK_V7K(3) — używana tylko, gdy nie wczytano oficjalnego schematu (plik i tak NIEZWALIDOWANY).</summary>
    public const string QuarterlyNamespacePlaceholder = "urn:pelna-ksiegowosc:jpk_v7k-3:wczytaj-oficjalny-schemat";
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
    public static JpkResult Build(Company company, DateOnly period, IReadOnlyList<VatRegisterEntry> sales, IReadOnlyList<VatRegisterEntry> purchases,
        VatDeclaration? declaration, VatDeclaration monthControl, bool correction, DateTime generatedUtc, XsdValidator validator)
    {
        var quarterly = company.VatPeriod == VatSettlementPeriod.Kwartalny;
        var schemaFile = quarterly ? JpkV7Rules.SchemaFileQuarterly : JpkV7Rules.SchemaFile;
        XNamespace ns = validator.TargetNamespace(schemaFile) ?? (quarterly ? JpkV7Rules.QuarterlyNamespacePlaceholder : JpkV7Rules.Ns.NamespaceName);
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(company.TaxOfficeCode)) throw new DomainException("Uzupełnij kod urzędu skarbowego w danych jednostki (wymagany w nagłówku JPK).");
        if (quarterly && period.Month % 3 == 0 && declaration is null) throw new DomainException("W ostatnim miesiącu kwartału JPK_V7K musi zawierać deklarację za kwartał.");
        if (quarterly && period.Month % 3 != 0) declaration = null;
        if (!quarterly && declaration is null) throw new DomainException("JPK_V7M wymaga części deklaracyjnej.");
        if (company.LegalForm == LegalForm.DzialalnoscGospodarczaOsobyFizycznej)
            throw new DomainException("Dla osoby fizycznej JPK wymaga sekcji OsobaFizyczna (imię, nazwisko, data urodzenia) — ten wariant nie jest jeszcze obsługiwany.");
        if (string.IsNullOrWhiteSpace(company.Email)) warnings.Add("Brak adresu e-mail jednostki — pole wymagane w Podmiot1.");
        if (declaration is not null) warnings.AddRange(declaration.Warnings);
        if (quarterly && declaration is null) warnings.Add($"JPK_V7K za {period:MM.yyyy}: tylko ewidencja — deklaracja za kwartał w pliku za {period.AddMonths(3 - (period.Month - 1) % 3 - 1):MM.yyyy}.");

        XElement E(string n, object? v) => new(ns + n, v);
        var naglowek = new XElement(ns + "Naglowek",
            new XElement(ns + "KodFormularza", new XAttribute("kodSystemowy", quarterly ? JpkV7Rules.QuarterlySystemCode : JpkV7Rules.SystemCode), new XAttribute("wersjaSchemy", JpkV7Rules.SchemaVersion), "JPK_VAT"),
            E("WariantFormularza", JpkV7Rules.Variant),
            E("DataWytworzeniaJPK", generatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", Inv)),
            E("NazwaSystemu", "Pełna Księgowość"),
            new XElement(ns + "CelZlozenia", new XAttribute("poz", "P_7"), correction ? "2" : "1"),
            E("KodUrzedu", company.TaxOfficeCode),
            E("Rok", period.Year),
            E("Miesiac", period.Month));
        var podmiot = new XElement(ns + "Podmiot1", new XAttribute("rola", "Podatnik"),
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
                    new XElement(ns + "KodFormularzaDekl", new XAttribute("kodSystemowy", quarterly ? JpkV7Rules.QuarterlyDeclarationSystemCode : JpkV7Rules.DeclarationSystemCode), new XAttribute("kodPodatku", "VAT"),
                        new XAttribute("rodzajZobowiazania", "Z"), new XAttribute("wersjaSchemy", JpkV7Rules.SchemaVersion), quarterly ? "VAT-7K" : "VAT-7"),
                    E("WariantFormularzaDekl", quarterly ? JpkV7Rules.QuarterlyDeclarationVariant : JpkV7Rules.DeclarationVariant)),
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
            AddKsefMarker(row, ns, f);
            if (f.JpkDocumentType is { Length: > 0 } t) row.Add(E("TypDokumentu", t));
            foreach (var gtu in (f.GtuCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) row.Add(E(gtu, "1"));
            foreach (var proc in (f.Procedures ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) row.Add(E(proc.Trim(), "1"));
            foreach (var fld in g.SelectMany(e => new[] { (e.JpkNetField, e.NetPln), (e.JpkTaxField, e.VatPln) }).Where(x => x.Item1 is not null)
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
            AddKsefMarker(row, ns, f);
            if (f.JpkDocumentType is { Length: > 0 } t) row.Add(E("DokumentZakupu", t));
            foreach (var fld in g.SelectMany(e => new[] { (e.JpkNetField, e.NetPln), (e.JpkTaxField, e.DeductibleVatPln) }).Where(x => x.Item1 is not null)
                         .GroupBy(x => x.Item1!).OrderBy(x => int.Parse(x.Key[2..], Inv)))
                row.Add(E(fld.Key, Money.Xml(fld.Sum(x => x.Item2))));
            ewidencja.Add(row);
        }
        ewidencja.Add(new XElement(ns + "ZakupCtrl", E("LiczbaWierszyZakupow", lp - 1), E("PodatekNaliczony", Money.Xml(monthControl.PurchaseTaxControl))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "JPK", new XAttribute(XNamespace.Xmlns + "tns", ns.NamespaceName), naglowek, podmiot, deklaracja, ewidencja));
        var validation = validator.Validate(doc, schemaFile, quarterly ? "JPK_V7K(3)" : "JPK_V7M(3)");
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
