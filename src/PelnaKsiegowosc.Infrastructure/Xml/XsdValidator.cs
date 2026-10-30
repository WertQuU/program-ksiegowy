using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace PelnaKsiegowosc.Infrastructure.Xml;

public sealed record XsdValidationResult(bool IsValid, IReadOnlyList<string> Errors, string SchemaDescription)
{
    public static XsdValidationResult SchemaMissing(string what) =>
        new(false, new[] { $"Brak oficjalnego schematu XSD ({what}) — plik NIE został zwalidowany. Wczytaj schemat w Ustawienia → Schematy XSD." }, "brak schematu");
}

/// <summary>
/// Walidacja XML względem oficjalnych schematów XSD — w pełni offline. Odwołania do http://crd.gov.pl/... są mapowane
/// na lokalne kopie (katalog schematów programu i katalog schematów wczytanych przez użytkownika). Dostęp do sieci
/// podczas walidacji jest zablokowany.
/// </summary>
public sealed class XsdValidator
{
    private readonly IReadOnlyList<string> _schemaDirectories;

    public XsdValidator(params string[] schemaDirectories) => _schemaDirectories = schemaDirectories.Where(Directory.Exists).ToList();

    public static string BundledSchemasDirectory => Path.Combine(AppContext.BaseDirectory, "schemas");

    public string? FindSchema(string fileName) =>
        _schemaDirectories.SelectMany(d => Directory.EnumerateFiles(d, fileName, SearchOption.AllDirectories)).FirstOrDefault();

    /// <summary>Przestrzeń nazw (targetNamespace) wczytanego schematu — pozwala generować plik zgodny z faktycznie posiadanym XSD.</summary>
    public string? TargetNamespace(string schemaFileName)
    {
        if (FindSchema(schemaFileName) is not { } path) return null;
        try { return (string?)XDocument.Load(path).Root?.Attribute("targetNamespace"); }
        catch (XmlException) { return null; }
    }

    public XsdValidationResult Validate(XDocument doc, string schemaFileName, string description)
    {
        var schemaPath = FindSchema(schemaFileName);
        if (schemaPath is null) return XsdValidationResult.SchemaMissing(description);
        var resolver = new LocalOnlyResolver(_schemaDirectories, Path.GetDirectoryName(schemaPath)!);
        var set = new XmlSchemaSet { XmlResolver = resolver };
        var errors = new List<string>();
        set.ValidationEventHandler += (_, e) => errors.Add("Schemat: " + e.Message);
        using (var r = XmlReader.Create(schemaPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = resolver }))
            set.Add(null, r);
        set.Compile();
        doc.Validate(set, (_, e) => errors.Add($"{(e.Severity == XmlSeverityType.Error ? "Błąd" : "Ostrzeżenie")}: {e.Message}"));
        return new XsdValidationResult(errors.Count == 0, errors, $"{description} ({Path.GetFileName(schemaPath)})");
    }

    /// <summary>Resolver zwracający wyłącznie pliki lokalne (nazwa pliku z adresu URL) — bez pobierania z Internetu.</summary>
    private sealed class LocalOnlyResolver : XmlResolver
    {
        private readonly IReadOnlyList<string> _dirs;
        private readonly string _primary;

        public LocalOnlyResolver(IReadOnlyList<string> dirs, string primary)
        {
            _dirs = dirs;
            _primary = primary;
        }

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
        {
            var name = Path.GetFileName(relativeUri ?? "");
            var local = Path.Combine(_primary, name);
            if (File.Exists(local)) return new Uri(local);
            var found = _dirs.SelectMany(d => Directory.EnumerateFiles(d, name, SearchOption.AllDirectories)).FirstOrDefault();
            if (found is not null) return new Uri(found);
            throw new XmlSchemaException($"Brak lokalnej kopii schematu importowanego „{relativeUri}”.");
        }

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            if (!absoluteUri.IsFile) throw new XmlSchemaException($"Pobieranie schematów z sieci jest zablokowane: {absoluteUri}");
            return File.OpenRead(absoluteUri.LocalPath);
        }
    }
}
