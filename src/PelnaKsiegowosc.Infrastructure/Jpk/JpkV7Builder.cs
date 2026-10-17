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
}

public sealed record JpkResult(XDocument Document, XsdValidationResult Validation, IReadOnlyList<string> Warnings)
{
    public string Status => Validation.IsValid ? "ZWALIDOWANY ze schematem XSD" : "WYGENEROWANY — NIEZWALIDOWANY (nie składać bez walidacji)";
}

/// <summary>Generator JPK_V7M z ewidencji VAT (rejestry) i wyliczonej deklaracji.</summary>
public static class JpkV7Builder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static JpkResult Build(Company company, DateOnly period, IReadOnlyList<VatRegisterEntry> sales, IReadOnlyList<VatRegisterEntry> purchases,
        VatDeclaration declaration, bool correction, DateTime generatedUtc, XsdValidator validator)
    {
        var ns = JpkV7Rules.Ns;
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(company.TaxOfficeCode)) throw new DomainException("Uzupełnij kod urzędu skarbowego w danych spółki (wymagany w nagłówku JPK).");
        if (company.VatPeriod != VatSettlementPeriod.Miesieczny) throw new DomainException("Spółka rozlicza VAT kwartalnie — JPK_V7K nie jest jeszcze obsługiwany.");
        if (string.IsNullOrWhiteSpace(company.Email)) warnings.Add("Brak adresu e-mail spółki — pole wymagane w Podmiot1.");
        warnings.AddRange(declaration.Warnings);

        XElement E(string n, object? v) => new(ns + n, v);
        var naglowek = new XElement(ns + "Naglowek",
            new XElement(ns + "KodFormularza", new XAttribute("kodSystemowy", JpkV7Rules.SystemCode), new XAttribute("wersjaSchemy", JpkV7Rules.SchemaVersion), "JPK_VAT"),
            E("WariantFormularza", JpkV7Rules.Variant),
            E("DataWytworzeniaJPK", generatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", Inv)),
            E("NazwaSystemu", "Pełna Księgowość PSA"),
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
        var pos = new XElement(ns + "PozycjeSzczegolowe");
        foreach (var (k, v) in declaration.Fields.OrderBy(f => int.Parse(f.Key[2..], Inv)))
            if (v != 0 || k is "P_38" or "P_51") pos.Add(E(k, v.ToString("0", Inv)));
        var deklaracja = new XElement(ns + "Deklaracja",
            new XElement(ns + "Naglowek",
                new XElement(ns + "KodFormularzaDekl", new XAttribute("kodSystemowy", JpkV7Rules.DeclarationSystemCode), new XAttribute("kodPodatku", "VAT"),
                    new XAttribute("rodzajZobowiazania", "Z"), new XAttribute("wersjaSchemy", JpkV7Rules.SchemaVersion), "VAT-7"),
                E("WariantFormularzaDekl", JpkV7Rules.DeclarationVariant)),
            pos,
            E("Pouczenia", "1"));

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
        ewidencja.Add(new XElement(ns + "SprzedazCtrl", E("LiczbaWierszySprzedazy", lp - 1), E("PodatekNalezny", Money.Xml(declaration.SalesTaxControl))));
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
        ewidencja.Add(new XElement(ns + "ZakupCtrl", E("LiczbaWierszyZakupow", lp - 1), E("PodatekNaliczony", Money.Xml(declaration.PurchaseTaxControl))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "JPK", new XAttribute(XNamespace.Xmlns + "tns", ns.NamespaceName), naglowek, podmiot, deklaracja, ewidencja));
        var validation = validator.Validate(doc, JpkV7Rules.SchemaFile, "JPK_V7M(3)");
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
