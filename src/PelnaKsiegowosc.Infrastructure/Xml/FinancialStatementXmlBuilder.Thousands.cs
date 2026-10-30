using System.Globalization;
using System.Xml.Linq;

namespace PelnaKsiegowosc.Infrastructure.Xml;

/// <summary>
/// Warianty „w tysiącach złotych”: SFJINT(2) (CRWDE 2025/08/04/13810), SFJMAT(2) (2025/09/10/13846), SFJMIT(2) (2025/08/11/13819).
/// Sprawozdanie budowane jest w złotych, a następnie kwoty zaokrąglane do pełnych tysięcy; różnice z zaokrągleń
/// (np. suma pozycji ≠ pozycja nadrzędna o 1) użytkownik koryguje zgodnie z przyjętą zasadą.
/// </summary>
public static partial class FinancialStatementXmlBuilder
{
    public const string SchemaFileInnaTys = "Schemat_SFJINT(2)_v1-0E.xsd";
    public const string SchemaFileMalaTys = "Schemat_SFJMAT(2)_v1-0E.xsd";
    public const string SchemaFileMikroTys = "Schemat_SFJMIT(2)_v1-0E.xsd";

    private static readonly HashSet<string> AmountNames = new() { "KwotaA", "KwotaB", "RB", "RP" };

    private static Result ToThousands(Result zl, StatementScope scope, XsdValidator validator)
    {
        var (from, to, unit, code, system, schema) = scope switch
        {
            StatementScope.Mala => (NsMala, (XNamespace)"http://crd.gov.pl/wzor/2025/09/10/13846/", "JednostkaMala", "SprFinJednostkaMalaWTysiacach", "SFJMAT (2)", SchemaFileMalaTys),
            StatementScope.Mikro => (NsMikro, (XNamespace)"http://crd.gov.pl/wzor/2025/08/11/13819/", "JednostkaMikro", "SprFinJednostkaMikroWTysiacach", "SFJMIT (2)", SchemaFileMikroTys),
            _ => (Ns, (XNamespace)"http://crd.gov.pl/wzor/2025/08/04/13810/", "JednostkaInna", "SprFinJednostkaInnaWTysiacach", "SFJINT (2)", SchemaFileInnaTys)
        };
        var doc = new XDocument(zl.Document);
        foreach (var e in doc.Descendants().ToList())
        {
            if (e.Name.Namespace == from) e.Name = to + (e.Name.LocalName == unit ? unit + "WTys" : e.Name.LocalName);
            if (AmountNames.Contains(e.Name.LocalName) && !e.HasElements &&
                decimal.TryParse(e.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                e.Value = Math.Round(v / 1000m, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            if (e.Name.LocalName == "KodSprawozdania")
            {
                e.Value = code;
                e.SetAttributeValue("kodSystemowy", system);
            }
        }
        var root = doc.Root!;
        foreach (var a in root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == from.NamespaceName).ToList()) a.Remove();
        var validation = validator.Validate(doc, schema, $"e-Sprawozdanie {system.Replace(" ", "")}");
        var notes = zl.NotImplemented.ToList();
        notes.Add("Kwoty zaokrąglono do pełnych tysięcy złotych — sprawdź zgodność sum (bilans: aktywa = pasywa) i w razie potrzeby skoryguj różnice z zaokrągleń.");
        return new Result(doc, validation, notes);
    }
}
