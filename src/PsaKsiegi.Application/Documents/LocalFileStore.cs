using System.Security.Cryptography;
using PsaKsiegi.Application.Abstractions;
using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Application.Documents;

/// <summary>
/// Lokalny magazyn załączników: pliki zapisywane pod nazwą będącą identyfikatorem (bez nazw od użytkownika —
/// ochrona przed path traversal), w strukturze rok/miesiąc. Zapis atomowy (plik tymczasowy + przeniesienie).
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    public LocalFileStore(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public async Task<(string storagePath, string sha256, long size)> SaveAsync(Stream content, string suggestedName, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rel = Path.Combine(now.Year.ToString("0000"), now.Month.ToString("00"), Guid.NewGuid().ToString("N") + ".bin");
        var full = Resolve(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                size += read;
            }
            await fs.FlushAsync(ct);
        }
        File.Move(tmp, full);
        return (rel.Replace('\\', '/'), Convert.ToHexString(sha.GetHashAndReset()), size);
    }

    public Task<Stream> OpenAsync(string storagePath, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(Resolve(storagePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));

    public Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default) => Task.FromResult(File.Exists(Resolve(storagePath)));

    private string Resolve(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(RootPath, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new DomainException("Nieprawidłowa ścieżka załącznika.");
        return full;
    }

    public static async Task<string> Sha256Async(Stream s, CancellationToken ct = default) =>
        Convert.ToHexString(await SHA256.HashDataAsync(s, ct));
}

/// <summary>Walidacja załączników: dozwolone typy (biała lista), rozmiar, zgodność sygnatury pliku z rozszerzeniem.</summary>
public static class AttachmentPolicy
{
    public const long MaxBytes = 50L * 1024 * 1024;

    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".xml"] = "application/xml",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".eml"] = "message/rfc822",
        [".sta"] = "text/plain",
        [".mt940"] = "text/plain"
    };

    public static string Validate(string fileName, ReadOnlySpan<byte> header, long size)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) throw new DomainException("Nieprawidłowa nazwa pliku.");
        if (size <= 0) throw new DomainException("Plik jest pusty.");
        if (size > MaxBytes) throw new DomainException($"Plik przekracza limit {MaxBytes / 1024 / 1024} MB.");
        var ext = Path.GetExtension(name);
        if (!Allowed.TryGetValue(ext, out var contentType))
            throw new DomainException($"Niedozwolony typ pliku „{ext}”. Dozwolone: {string.Join(", ", Allowed.Keys)}.");
        if (header.Length >= 2 && header[0] == 'M' && header[1] == 'Z') throw new DomainException("Plik wykonywalny nie może być załącznikiem.");
        var ok = ext.ToLowerInvariant() switch
        {
            ".pdf" => header.StartsWith("%PDF"u8),
            ".png" => header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            ".jpg" or ".jpeg" => header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ".xlsx" or ".docx" or ".odt" or ".ods" => header.StartsWith("PK"u8),
            ".tif" or ".tiff" => header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8),
            _ => true
        };
        if (!ok) throw new DomainException($"Zawartość pliku nie odpowiada rozszerzeniu „{ext}”.");
        return contentType;
    }
}
