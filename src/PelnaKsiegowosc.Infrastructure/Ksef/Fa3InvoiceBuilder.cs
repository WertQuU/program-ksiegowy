using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Validation;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Xml;

namespace PelnaKsiegowosc.Infrastructure.Ksef;

/// <summary>
/// Budowa faktury ustrukturyzowanej w schemacie FA(3) (namespace http://crd.gov.pl/wzor/2025/06/25/13775/, kodSystemowy „FA (3)”,
/// wersjaSchemy „1-0E”) na podstawie dokumentu sprzedaży. Wynik jest walidowany względem oficjalnego XSD Ministerstwa Finansów.
/// Zakres: faktura VAT i korekta (KOR) w PLN lub walucie; bez zaliczek, marży, nowych środków transportu, Podmiotu3.
/// </summary>
public static class Fa3InvoiceBuilder
{
    public static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2025/06/25/13775/";
    public const string SchemaFile = "schemat_FA(3)_v1-0E.xsd";
    public const string SystemCode = "FA (3)";
    public const string SchemaVersion = "1-0E";
    public const string FormValue = "FA";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Input(Document Invoice, Company Seller, Counterparty Buyer, Document? CorrectedInvoice, DateTime GeneratedUtc);

    public static XDocument Build(Input input)
    {
        var d = input.Invoice;
        var s = input.Seller;
        var b = input.Buyer;
        if (!d.IsSales) throw new DomainException("Do KSeF wysyła się faktury sprzedaży.");
        if (d.Lines.Count == 0) throw new DomainException("Faktura nie ma pozycji.");
        if (d.VatLines.Any(v => v.VatRateCode == "zw") && string.IsNullOrWhiteSpace(d.VatExemptionBasis))
            throw new DomainException("Faktura ze stawką „zw” wymaga podania podstawy zwolnienia (P_19A).");

        X E(string name, params object?[] content) => new(Ns + name, content.Where(c => c is not null));

        var naglowek = E("Naglowek",
            E("KodFormularza", new XAttribute("kodSystemowy", SystemCode), new XAttribute("wersjaSchemy", SchemaVersion), FormValue),
            E("WariantFormularza", "3"),
            E("DataWytworzeniaFa", input.GeneratedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", Inv)),
            E("SystemInfo", "Pełna Księgowość P.S.A."));

        var podmiot1 = E("Podmiot1",
            E("DaneIdentyfikacyjne", E("NIP", Identifiers.NormalizeNip(s.Nip)), E("Nazwa", Trunc(s.FullName, 512))),
            E("Adres", E("KodKraju", "PL"), E("AdresL1", Trunc(s.AddressLine1, 512)), E("AdresL2", Trunc(s.AddressLine2, 512))),
            string.IsNullOrWhiteSpace(s.Email) && string.IsNullOrWhiteSpace(s.Phone) ? null
                : E("DaneKontaktowe", string.IsNullOrWhiteSpace(s.Email) ? null : E("Email", s.Email), string.IsNullOrWhiteSpace(s.Phone) ? null : E("Telefon", Trunc(s.Phone!, 16))));

        object buyerId;
        if (b.CountryCode == "PL" && !string.IsNullOrWhiteSpace(b.Nip)) buyerId = E("NIP", Identifiers.NormalizeNip(b.Nip));
        else if (!string.IsNullOrWhiteSpace(b.VatEuNumber) && IsEu(b.CountryCode))
            buyerId = new object[] { E("KodUE", b.CountryCode == "GR" ? "EL" : b.CountryCode), E("NrVatUE", StripCountry(b.VatEuNumber!, b.CountryCode)) };
        else if (!string.IsNullOrWhiteSpace(b.VatEuNumber)) buyerId = new object[] { E("KodKraju", b.CountryCode), E("NrID", b.VatEuNumber) };
        else buyerId = E("BrakID", "1");

        var podmiot2 = E("Podmiot2",
            E("DaneIdentyfikacyjne", buyerId, E("Nazwa", Trunc(d.CounterpartyName ?? b.Name, 512))),
            string.IsNullOrWhiteSpace(b.City) ? null : E("Adres", E("KodKraju", b.CountryCode), E("AdresL1", Trunc($"{b.Street} {b.BuildingNumber}{(string.IsNullOrWhiteSpace(b.ApartmentNumber) ? "" : "/" + b.ApartmentNumber)}".Trim(), 512)),
                E("AdresL2", Trunc($"{b.PostalCode} {b.City}".Trim(), 512))),
            E("JST", "2"),
            E("GV", "2"));

        var foreign = d.Currency != "PLN";
        var fa = new List<object?>
        {
            E("KodWaluty", d.Currency),
            E("P_1", D(d.IssueDate)),
            string.IsNullOrWhiteSpace(s.City) ? null : E("P_1M", s.City),
            E("P_2", d.Number),
            d.OperationDate != default && d.OperationDate != d.IssueDate ? E("P_6", D(d.OperationDate)) : null
        };
        // podsumowania wg stawek
        foreach (var (net, tax, taxW, codes) in new (string, string, string?, string[])[]
                 {
                     ("P_13_1", "P_14_1", "P_14_1W", new[] { "23" }), ("P_13_2", "P_14_2", "P_14_2W", new[] { "8" }), ("P_13_3", "P_14_3", "P_14_3W", new[] { "5" })
                 })
        {
            var vl = d.VatLines.Where(v => codes.Contains(v.VatRateCode)).ToList();
            if (vl.Count == 0) continue;
            fa.Add(E(net, A(vl.Sum(v => v.NetAmount))));
            fa.Add(E(tax, A(vl.Sum(v => v.VatAmount))));
            if (foreign) fa.Add(E(taxW!, A(vl.Sum(v => v.VatAmountPln))));
        }
        foreach (var (field, code) in new[] { ("P_13_6_1", "0"), ("P_13_6_2", "0WDT"), ("P_13_6_3", "0EX"), ("P_13_7", "zw"), ("P_13_8", "npI"), ("P_13_9", "npII"), ("P_13_10", "oo") })
        {
            var vl = d.VatLines.Where(v => v.VatRateCode == code).ToList();
            if (vl.Count > 0) fa.Add(E(field, A(vl.Sum(v => v.NetAmount))));
        }
        fa.Add(E("P_15", A(d.GrossAmount)));
        if (foreign) fa.Add(E("KursWalutyZ", d.ExchangeRate.ToString("0.######", Inv)));

        var hasZw = d.VatLines.Any(v => v.VatRateCode == "zw");
        fa.Add(E("Adnotacje",
            E("P_16", d.CashMethod ? "1" : "2"),
            E("P_17", d.SelfBilling ? "1" : "2"),
            E("P_18", d.ReverseCharge || d.VatLines.Any(v => v.VatRateCode == "oo") ? "1" : "2"),
            E("P_18A", d.SplitPayment ? "1" : "2"),
            E("Zwolnienie", hasZw ? new object[] { E("P_19", "1"), E("P_19A", Trunc(d.VatExemptionBasis!, 256)) } : E("P_19N", "1")),
            E("NoweSrodkiTransportu", E("P_22N", "1")),
            E("P_23", "2"),
            E("PMarzy", E("P_PMarzyN", "1"))));
        fa.Add(E("RodzajFaktury", d.IsCorrection ? "KOR" : "VAT"));
        if (d.IsCorrection)
        {
            var orig = input.CorrectedInvoice ?? throw new DomainException("Brak faktury korygowanej.");
            fa.Add(E("PrzyczynaKorekty", Trunc(d.CorrectionReason ?? "", 256)));
            fa.Add(E("DaneFaKorygowanej",
                E("DataWystFaKorygowanej", D(orig.IssueDate)),
                E("NrFaKorygowanej", orig.Number),
                string.IsNullOrWhiteSpace(orig.KsefNumber) ? E("NrKSeFN", "1") : new object[] { E("NrKSeF", "1"), E("NrKSeFFaKorygowanej", orig.KsefNumber) }));
        }
        foreach (var l in d.Lines.OrderBy(l => l.LineNo))
        {
            var rate = VatRules.Get(l.VatRateCode, d.OperationDate == default ? d.IssueDate : d.OperationDate);
            fa.Add(E("FaWiersz",
                E("NrWierszaFa", l.LineNo.ToString(Inv)),
                E("P_7", Trunc(l.Name, 512)),
                string.IsNullOrWhiteSpace(l.PkwiuOrCn) ? null : E("PKWiU", Trunc(l.PkwiuOrCn!, 50)),
                E("P_8A", Trunc(l.Unit, 256)),
                E("P_8B", l.Quantity.ToString("0.######", Inv)),
                E("P_9A", l.UnitNetPrice.ToString("0.########", Inv)),
                E("P_11", A(l.NetAmount)),
                E("P_12", rate.Fa3Code),
                l.GtuCode is null ? null : E("GTU", "GTU_" + l.GtuCode)));
        }
        var platnosc = new List<object?>();
        if (d.DueDate is { } due) platnosc.Add(E("TerminPlatnosci", E("Termin", D(due))));
        platnosc.Add(E("FormaPlatnosci", d.PaymentMethod switch { PaymentMethod.Gotowka => "1", PaymentMethod.Karta => "2", PaymentMethod.Kompensata => "6", _ => "6" }));
        if (!string.IsNullOrWhiteSpace(d.BankAccountIban))
        {
            var iban = Identifiers.NormalizeIban(d.BankAccountIban);
            platnosc.Add(E("RachunekBankowy", E("NrRB", iban.StartsWith("PL") ? iban[2..] : iban)));
        }
        fa.Add(E("Platnosc", platnosc.ToArray()));

        var stopka = new List<object?>();
        if (!string.IsNullOrWhiteSpace(s.InvoiceFooter)) stopka.Add(E("Informacje", E("StopkaFaktury", Trunc(s.InvoiceFooter!, 3500))));
        if (!string.IsNullOrWhiteSpace(s.Krs) || !string.IsNullOrWhiteSpace(s.Regon))
            stopka.Add(E("Rejestry", E("PelnaNazwa", Trunc(s.FullName, 256)),
                string.IsNullOrWhiteSpace(s.Krs) ? null : E("KRS", s.Krs),
                string.IsNullOrWhiteSpace(s.Regon) ? null : E("REGON", Identifiers.Digits(s.Regon))));

        var root = E("Faktura", new XAttribute(XNamespace.Xmlns + "tns", Ns.NamespaceName), naglowek, podmiot1, podmiot2, E("Fa", fa.ToArray()),
            stopka.Count > 0 ? E("Stopka", stopka.ToArray()) : null);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    /// <summary>Serializacja zgodna z wymaganiami KSeF: UTF-8 bez BOM, bez instrukcji przetwarzania.</summary>
    public static byte[] ToBytes(XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false }))
            doc.Save(w);
        return ms.ToArray();
    }

    public static XsdValidationResult Validate(XDocument doc, XsdValidator validator) => validator.Validate(doc, SchemaFile, "FA(3) v1-0E");

    private static bool IsEu(string cc) => new[] { "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GR", "EL", "HR", "HU", "IE", "IT", "LT", "LU", "LV", "MT", "NL", "PT", "RO", "SE", "SI", "SK", "XI" }.Contains(cc);
    private static string StripCountry(string vat, string cc) => vat.Replace(" ", "").ToUpperInvariant() is var v && v.StartsWith(cc == "GR" ? "EL" : cc) ? v[2..] : vat.Replace(" ", "").ToUpperInvariant();
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);
    private static string A(decimal v) => Money.Xml(v);
    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    private sealed class X : XElement
    {
        public X(XName name, IEnumerable<object?> content) : base(name, Flatten(content).ToArray()) { }

        private static IEnumerable<object> Flatten(IEnumerable<object?> items)
        {
            foreach (var i in items)
            {
                if (i is null) continue;
                if (i is object[] arr) { foreach (var x in Flatten(arr)) yield return x; }
                else yield return i;
            }
        }
    }
}
