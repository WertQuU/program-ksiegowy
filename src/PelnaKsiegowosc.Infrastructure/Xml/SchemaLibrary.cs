using System.IO.Compression;
using System.Text;
using System.Xml;
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
    private const int MaxSchemaBytes = 8 * 1024 * 1024;
    private const int MaxArchiveSchemas = 128;
    private const long MaxArchiveBytes = 64L * 1024 * 1024;
    public static readonly IReadOnlyList<KnownSchema> Known = new KnownSchema[]
    {
        new("FA(3) — e-Faktura KSeF", Ksef.Fa3InvoiceBuilder.SchemaFile, null,
            "https://github.com/CIRFMF/ksef-docs", "Faktury wysyłane do KSeF (schemat dołączony do programu)"),
        new("JPK_V7M(3)", Jpk.JpkV7Rules.SchemaFile, "https://crd.gov.pl/wzor/2025/12/19/14090/schemat.xsd",
            "https://www.podatki.gov.pl/jednolity-plik-kontrolny/jpk_vat-z-deklaracja/", "Ewidencja VAT z deklaracją — rozliczenie miesięczne"),
        new("JPK_V7K(3)", Jpk.JpkV7Rules.SchemaFileQuarterly, "https://crd.gov.pl/wzor/2025/12/19/14089/schemat.xsd",
            "https://www.podatki.gov.pl/jednolity-plik-kontrolny/jpk_vat-z-deklaracja/", "Ewidencja VAT z deklaracją — rozliczenie kwartalne"),
        new("JPK_CIT (JPK_KR_PD)", Jpk.JpkKrPdBuilder.SchemaFile, "https://www.gov.pl/attachment/4ec8f106-032c-42f2-9fb5-15b5ec5ca670",
            "https://www.podatki.gov.pl/jednolity-plik-kontrolny/", "Księgi rachunkowe ze znacznikami podatkowymi — CIT (od roku 2026 dla podatników JPK_V7)"),
        new("e-Sprawozdanie SFJINZ(2) — jednostka inna, w złotych", FinancialStatementXmlBuilder.SchemaFile, "https://crd.gov.pl/wzor/2025/08/07/13817/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie finansowe w formie elektronicznej (art. 45 ust. 1g UoR)"),
        new("e-Sprawozdanie SFJMAZ(2) — jednostka mała, w złotych", FinancialStatementXmlBuilder.SchemaFileMala, "https://crd.gov.pl/wzor/2025/08/07/13818/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie jednostki małej korzystającej z uproszczeń (zał. nr 5 UoR)"),
        new("e-Sprawozdanie SFJMIZ(2) — jednostka mikro, w złotych", FinancialStatementXmlBuilder.SchemaFileMikro, "https://crd.gov.pl/wzor/2025/08/12/13821/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie jednostki mikro (zał. nr 4 UoR)"),
        new("e-Sprawozdanie SFJINT(2) — jednostka inna, w tysiącach zł", FinancialStatementXmlBuilder.SchemaFileInnaTys, "https://crd.gov.pl/wzor/2025/08/04/13810/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie w tysiącach złotych (zał. nr 1 UoR)"),
        new("e-Sprawozdanie SFJMAT(2) — jednostka mała, w tysiącach zł", FinancialStatementXmlBuilder.SchemaFileMalaTys, "https://crd.gov.pl/wzor/2025/09/10/13846/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie jednostki małej w tysiącach złotych (zał. nr 5 UoR)"),
        new("e-Sprawozdanie SFJMIT(2) — jednostka mikro, w tysiącach zł", FinancialStatementXmlBuilder.SchemaFileMikroTys, "https://crd.gov.pl/wzor/2025/08/11/13819/schemat.xsd",
            "https://www.podatki.gov.pl/e-sprawozdania-finansowe/", "Sprawozdanie jednostki mikro w tysiącach złotych (zał. nr 4 UoR)"),
        new("VAT-UE(5)", Declarations.VatUeXmlBuilder.SchemaFile, "https://crd.gov.pl/wzor/2021/01/12/10293/schemat.xsd",
            "https://www.podatki.gov.pl/e-deklaracje/dokumentacja-it/struktury-dokumentow-xml/", "Informacja podsumowująca VAT-UE"),
        new("VAT-UEK(5)", Declarations.VatUeXmlBuilder.SchemaFileCorrection, "https://crd.gov.pl/wzor/2021/01/26/10316/schemat.xsd",
            "https://www.podatki.gov.pl/e-deklaracje/dokumentacja-it/struktury-dokumentow-xml/", "Korekta informacji podsumowującej VAT-UEK"),
        new("PIT-11(29)", Declarations.Pit11XmlBuilder.SchemaFile, "https://crd.gov.pl/wzor/2022/11/09/11890/schemat.xsd",
            "https://www.podatki.gov.pl/e-deklaracje/dokumentacja-it/struktury-dokumentow-xml/", "Informacja o dochodach i pobranych zaliczkach"),
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
        CheckOfficialUrl(url);
        using var response = await _http!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // HttpClient może obsługiwać przekierowania — nie zaakceptuj treści z innej domeny ani HTTP.
        CheckOfficialUrl(response.RequestMessage?.RequestUri ?? url);
        if (response.Content.Headers.ContentLength is > MaxSchemaBytes)
            throw new InvalidDataException("Pobrany schemat XSD przekracza limit 8 MiB.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (bytes.Length + read > MaxSchemaBytes)
                throw new InvalidDataException("Pobrany schemat XSD przekracza limit 8 MiB.");
            bytes.Write(chunk, 0, read);
        }
        var text = Encoding.UTF8.GetString(bytes.ToArray());
        var doc = ParseSchema(bytes.ToArray());
        Directory.CreateDirectory(_userDirectory);
        var target = Path.Combine(_userDirectory, SafeName(fileName));
        await File.WriteAllTextAsync(target, text, ct);
        saved.Add(target);
        foreach (var location in ImportedLocations(doc))
        {
            var child = new Uri(url, location);
            // Niektóre oficjalne schematy nadal deklarują importy przez HTTP.
            // Przed pobraniem podnosimy transport do HTTPS tylko dla domen urzędowych.
            if (child.Scheme == Uri.UriSchemeHttp &&
                (child.Host.Equals("gov.pl", StringComparison.OrdinalIgnoreCase) ||
                 child.Host.EndsWith(".gov.pl", StringComparison.OrdinalIgnoreCase)))
                child = new UriBuilder(child) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
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
                var entries = zip.Entries.Where(e => e.Name.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)).ToList();
                if (entries.Count > MaxArchiveSchemas)
                    throw new InvalidDataException("Archiwum zawiera zbyt wiele schematów XSD (limit 128).");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var e in entries)
                {
                    var name = SafeName(e.Name);
                    if (!names.Add(name))
                        throw new InvalidDataException($"Archiwum zawiera powtarzający się schemat XSD: {name}.");
                    if (e.Length > MaxSchemaBytes || total + e.Length > MaxArchiveBytes)
                        throw new InvalidDataException("Archiwum XSD przekracza limit rozmiaru.");
                    using var source = e.Open();
                    using var bytes = new MemoryStream();
                    var chunk = new byte[64 * 1024];
                    int read;
                    while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (bytes.Length + read > MaxSchemaBytes || total + bytes.Length + read > MaxArchiveBytes)
                            throw new InvalidDataException("Rozpakowany schemat XSD przekracza limit rozmiaru.");
                        bytes.Write(chunk, 0, read);
                    }
                    total += bytes.Length;
                    saved.Add(Store(name, bytes.ToArray()));
                }
            }
            else
            {
                if (new FileInfo(path).Length > MaxSchemaBytes)
                    throw new InvalidDataException("Schemat XSD przekracza limit 8 MiB.");
                saved.Add(Store(Path.GetFileName(path), File.ReadAllBytes(path)));
            }
        }
        return saved;
    }

    private static void CheckOfficialUrl(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps ||
            !(url.Host.Equals("gov.pl", StringComparison.OrdinalIgnoreCase) ||
              url.Host.EndsWith(".gov.pl", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Schematy można pobierać wyłącznie przez HTTPS z domen gov.pl: {url}");
    }

    private static XDocument ParseSchema(byte[] content)
    {
        using var reader = XmlReader.Create(new MemoryStream(content),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSchemaBytes });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != XName.Get("schema", "http://www.w3.org/2001/XMLSchema"))
            throw new InvalidDataException("Plik nie jest schematem XSD.");
        return document;
    }

    private string Store(string name, byte[] content)
    {
        if (content.Length > MaxSchemaBytes)
            throw new InvalidDataException("Schemat XSD przekracza limit 8 MiB.");
        try { ParseSchema(content); }
        catch (Exception e) when (e is XmlException or InvalidDataException)
        {
            throw new InvalidOperationException($"Plik {name} nie jest poprawnym dokumentem XSD: {e.Message}", e);
        }
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
