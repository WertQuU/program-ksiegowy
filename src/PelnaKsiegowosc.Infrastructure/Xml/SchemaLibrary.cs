using System.IO.Compression;
using System.Xml.Linq;

namespace PelnaKsiegowosc.Infrastructure.Xml;

/// <summary>Oficjalny schemat XSD używany do walidacji plików wysyłanych do MF / KRS.</summary>
public sealed record KnownSchema(string Name, string FileName, string? DownloadUrl, string InfoUrl, string Purpose);

public sealed record SchemaStatus(KnownSchema Schema, string? Path)
{
    public bool Available => Path is not null;
}

/// <summary>
/// Biblioteka schematów XSD: stan (dołączony / wczytany / brak), pobranie z serwera MF (crd.gov.pl) wraz ze schematami
/// importowanymi oraz wczytanie z pliku .xsd lub archiwum .zip pobranego ręcznie ze strony MF. Walidacja odbywa się
/// później offline (<see cref="XsdValidator"/>).
/// </summary>
public sealed class SchemaLibrary
{
    public static readonly IReadOnlyList<KnownSchema> Known = new KnownSchema[]
    {
        new("FA(3) — e-Faktura KSeF", Ksef.Fa3InvoiceBuilder.SchemaFile, null,
            "https://github.com/CIRFMF/ksef-docs", "Faktury wysyłane do KSeF (schemat dołączony do programu)"),
        new("JPK_V7M(3)", Jpk.JpkV7Rules.SchemaFile, "http://crd.gov.pl/wzor/2025/12/19/14090/schemat.xsd",
            "https://www.podatki.gov.pl/jednolity-plik-kontrolny/jpk_vat-z-deklaracja/", "Ewidencja VAT z deklaracją — rozliczenie miesięczne"),
        new("JPK_V7K(3)", Jpk.JpkV7Rules.SchemaFileQuarterly, "http://crd.gov.pl/wzor/2025/12/19/14089/schemat.xsd",
            "https://www.podatki.gov.pl/jednolity-plik-kontrolny/jpk_vat-z-deklaracja/", "Ewidencja VAT z deklaracją — rozliczenie kwartalne"),
        new("JPK_CIT (JPK_KR_PD)", Jpk.JpkKrPdBuilder.SchemaFile, "https://www.gov.pl/attachment/4ec8f106-032c-42f2-9fb5-15b5ec5ca670",
            "https://www.podatki.gov.pl/jednolity-plik-kontrolny/", "Księgi rachunkowe ze znacznikami podatkowymi — CIT (od roku 2026 dla podatników JPK_V7)"),
        new("e-Sprawozdanie SFJINZ(2) — jednostka inna, w złotych", FinancialStatementXmlBuilder.SchemaFile, "http://crd.gov.pl/wzor/2025/08/07/13817/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie finansowe w formie elektronicznej (art. 45 ust. 1g UoR)"),
        new("e-Sprawozdanie SFJMAZ(2) — jednostka mała, w złotych", FinancialStatementXmlBuilder.SchemaFileMala, "http://crd.gov.pl/wzor/2025/08/07/13818/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie jednostki małej korzystającej z uproszczeń (zał. nr 5 UoR)"),
        new("e-Sprawozdanie SFJMIZ(2) — jednostka mikro, w złotych", FinancialStatementXmlBuilder.SchemaFileMikro, "http://crd.gov.pl/wzor/2025/08/12/13821/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie jednostki mikro (zał. nr 4 UoR)"),
    };

    private readonly string _userDirectory;
    private readonly IReadOnlyList<string> _directories;
    private IEnumerable<string> _searchDirectories => _directories.Where(Directory.Exists);
    private readonly HttpClient? _http;

    public SchemaLibrary(string userDirectory, HttpClient? http = null, params string[] extraSearchDirectories)
    {
        _userDirectory = userDirectory;
        _http = http;
        _directories = new[] { userDirectory }.Concat(extraSearchDirectories).ToList();
    }

    private string? Find(string fileName) =>
        _searchDirectories.SelectMany(d => Directory.EnumerateFiles(d, fileName, SearchOption.AllDirectories)).FirstOrDefault();

    public IReadOnlyList<SchemaStatus> Status() => Known.Select(k => new SchemaStatus(k, Find(k.FileName))).ToList();

    /// <summary>Pobiera schemat główny i rekurencyjnie schematy importowane (tylko z gov.pl). Zwraca listę zapisanych plików.</summary>
    public async Task<List<string>> DownloadAsync(KnownSchema schema, CancellationToken ct = default)
    {
        if (_http is null) throw new InvalidOperationException("Brak klienta HTTP.");
        if (schema.DownloadUrl is null) throw new InvalidOperationException($"Dla schematu {schema.Name} nie ma stałego adresu — pobierz go ze strony {schema.InfoUrl} i wczytaj z pliku.");
        var saved = new List<string>();
        await FetchAsync(new Uri(schema.DownloadUrl), schema.FileName, saved, 0, ct);
        return saved;
    }

    private async Task FetchAsync(Uri url, string fileName, List<string> saved, int depth, CancellationToken ct)
    {
        if (depth > 6) return;
        if (!url.Host.EndsWith("gov.pl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Odmowa pobrania schematu spoza domeny gov.pl: {url}");
        var text = await _http!.GetStringAsync(url, ct);
        var doc = XDocument.Parse(text);
        Directory.CreateDirectory(_userDirectory);
        var target = Path.Combine(_userDirectory, SafeName(fileName));
        await File.WriteAllTextAsync(target, text, ct);
        saved.Add(target);
        foreach (var location in ImportedLocations(doc))
        {
            var child = new Uri(url, location);
            var childName = SafeName(Path.GetFileName(child.LocalPath));
            if (Find(childName) is not null || saved.Any(s => Path.GetFileName(s) == childName)) continue;
            await FetchAsync(child, childName, saved, depth + 1, ct);
        }
    }

    public static IEnumerable<string> ImportedLocations(XDocument xsd)
    {
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        return xsd.Descendants().Where(e => e.Name == xs + "import" || e.Name == xs + "include")
            .Select(e => (string?)e.Attribute("schemaLocation")).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).Distinct();
    }

    /// <summary>Wczytuje pliki .xsd lub archiwa .zip (wyłącznie pliki .xsd, bez ścieżek z archiwum). Zwraca zapisane nazwy.</summary>
    public List<string> Import(IEnumerable<string> paths)
    {
        Directory.CreateDirectory(_userDirectory);
        var saved = new List<string>();
        foreach (var path in paths)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var e in zip.Entries.Where(e => e.Name.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)))
                {
                    using var s = e.Open();
                    var bytes = new MemoryStream();
                    s.CopyTo(bytes);
                    saved.Add(Store(e.Name, bytes.ToArray()));
                }
            }
            else saved.Add(Store(Path.GetFileName(path), File.ReadAllBytes(path)));
        }
        return saved;
    }

    private string Store(string name, byte[] content)
    {
        try { XDocument.Load(new MemoryStream(content)); }
        catch (Exception e) { throw new InvalidOperationException($"Plik {name} nie jest poprawnym dokumentem XML/XSD: {e.Message}"); }
        var target = Path.Combine(_userDirectory, SafeName(name));
        File.WriteAllBytes(target, content);
        return Path.GetFileName(target);
    }

    private static string SafeName(string name)
    {
        var n = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(n) || n.StartsWith('.')) throw new InvalidOperationException($"Nieprawidłowa nazwa pliku schematu: {name}");
        return string.Concat(n.Split(Path.GetInvalidFileNameChars()));
    }

    /// <summary>Brakujące schematy importowane przez wczytane pliki (do komunikatu dla użytkownika).</summary>
    public List<string> MissingImports()
    {
        var missing = new List<string>();
        foreach (var file in _searchDirectories.SelectMany(d => Directory.EnumerateFiles(d, "*.xsd", SearchOption.AllDirectories)))
        {
            XDocument doc;
            try { doc = XDocument.Load(file); } catch { continue; }
            foreach (var loc in ImportedLocations(doc))
            {
                var name = Path.GetFileName(new Uri(new Uri("http://x/"), loc).LocalPath);
                if (Find(name) is null && !missing.Contains(name)) missing.Add(name);
            }
        }
        return missing;
    }
}
