using System.Security.Cryptography;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Application.Documents;

/// <summary>
/// Lokalny magazyn załączników: pliki zapisywane pod nazwą będącą identyfikatorem (bez nazw od użytkownika —
/// ochrona przed path traversal), w strukturze rok/miesiąc. Zapis atomowy (plik tymczasowy + przeniesienie).
/// Z kluczem — treść szyfrowana AES-256-GCM (<see cref="AttachmentCrypto"/>); suma SHA-256 dotyczy treści jawnej, więc kontrola
/// integralności załączników działa bez zmian. Pliki zapisane wcześniej jawnie są odczytywane i mogą zostać zaszyfrowane
/// (<see cref="EncryptLegacyFilesAsync"/>). Katalog magazynu — dostęp tylko dla właściciela (Linux 0700).
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    private readonly byte[]? _key;

    public LocalFileStore(string rootPath, byte[]? key = null)
    {
        RootPath = Path.GetFullPath(rootPath);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(RootPath);
        else Directory.CreateDirectory(RootPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (key is not null && key.Length != 32) throw new ArgumentException("Klucz załączników musi mieć 256 bitów.", nameof(key));
        _key = key;
    }

    public string RootPath { get; }
    public bool Encrypted => _key is not null;

    public async Task<(string storagePath, string sha256, long size)> SaveAsync(Stream content, string suggestedName, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rel = Path.Combine(now.Year.ToString("0000"), now.Month.ToString("00"), Guid.NewGuid().ToString("N") + ".bin").Replace('\\', '/');
        var (sha, size) = await SaveAtAsync(rel, content, ct);
        return (rel, sha, size);
    }

    /// <summary>Zapis pod wskazaną ścieżką magazynu (odtwarzanie kopii, migracja baz) — z szyfrowaniem, jeśli magazyn ma klucz.</summary>
    public async Task<(string sha256, long size)> SaveAtAsync(string storagePath, Stream content, CancellationToken ct = default)
    {
        var full = Resolve(storagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var plain = new MemoryStream();
        await content.CopyToAsync(plain, ct);
        var bytes = plain.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var tmp = full + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            if (_key is null) await fs.WriteAsync(bytes, ct);
            else await AttachmentCrypto.EncryptAsync(bytes, fs, _key, ct);
            await fs.FlushAsync(ct);
        }
        File.Move(tmp, full, overwrite: true);
        return (sha, bytes.LongLength);
    }

    public async Task<Stream> OpenAsync(string storagePath, CancellationToken ct = default)
    {
        var full = Resolve(storagePath);
        var data = await File.ReadAllBytesAsync(full, ct);
        if (!AttachmentCrypto.IsEncrypted(data)) return new MemoryStream(data, writable: false);
        if (_key is null) throw new DomainException("Załącznik jest zaszyfrowany, a na tym stanowisku brak klucza załączników.");
        return new MemoryStream(AttachmentCrypto.Decrypt(data, _key), writable: false);
    }

    public Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default) => Task.FromResult(File.Exists(Resolve(storagePath)));

    /// <summary>Szyfruje pliki zapisane wcześniej jawnie (zachowując ścieżki i sumy SHA-256 treści). Zwraca liczbę zaszyfrowanych plików.</summary>
    public async Task<int> EncryptLegacyFilesAsync(CancellationToken ct = default)
    {
        if (_key is null || !Directory.Exists(RootPath)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(RootPath, "*.bin", SearchOption.AllDirectories))
        {
            var data = await File.ReadAllBytesAsync(f, ct);
            if (AttachmentCrypto.IsEncrypted(data)) continue;
            var tmp = f + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                await AttachmentCrypto.EncryptAsync(data, fs, _key, ct);
            File.Move(tmp, f, overwrite: true);
            n++;
        }
        return n;
    }

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

/// <summary>
/// Szyfrowanie załączników: nagłówek „PKZ1”, losowy nonce bazowy, porcje po 1 MiB szyfrowane AES-256-GCM (nonce = bazowy ⊕ numer
/// porcji; dane dodatkowe: numer porcji i znacznik ostatniej porcji — wykrycie zamiany kolejności i obcięcia pliku).
/// </summary>
public static class AttachmentCrypto
{
    private static readonly byte[] Magic = "PKZ1"u8.ToArray();
    private const int Chunk = 1024 * 1024;

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    public static async Task EncryptAsync(byte[] plain, Stream output, byte[] key, CancellationToken ct = default)
    {
        var baseNonce = RandomNumberGenerator.GetBytes(12);
        await output.WriteAsync(Magic, ct);
        await output.WriteAsync(baseNonce, ct);
        using var gcm = new AesGcm(key, 16);
        var count = Math.Max(1, (plain.Length + Chunk - 1) / Chunk);
        for (var i = 0; i < count; i++)
        {
            var start = i * Chunk;
            var len = Math.Min(Chunk, plain.Length - start);
            var cipher = new byte[len];
            var tag = new byte[16];
            gcm.Encrypt(Nonce(baseNonce, i), plain.AsSpan(start, len), cipher, tag, Aad(i, i == count - 1));
            await output.WriteAsync(BitConverter.GetBytes(len), ct);
            await output.WriteAsync(cipher, ct);
            await output.WriteAsync(tag, ct);
        }
    }

    public static byte[] Decrypt(byte[] data, byte[] key)
    {
        if (!IsEncrypted(data) || data.Length < 16) throw new DomainException("Nieprawidłowy format zaszyfrowanego załącznika.");
        var baseNonce = data.AsSpan(4, 12).ToArray();
        using var gcm = new AesGcm(key, 16);
        using var plain = new MemoryStream();
        var pos = 16;
        var i = 0;
        while (pos < data.Length)
        {
            if (pos + 4 > data.Length) throw new DomainException("Załącznik zaszyfrowany jest uszkodzony (obcięty).");
            var len = BitConverter.ToInt32(data, pos);
            pos += 4;
            if (len < 0 || len > Chunk || pos + len + 16 > data.Length) throw new DomainException("Załącznik zaszyfrowany jest uszkodzony.");
            var last = pos + len + 16 == data.Length;
            var buf = new byte[len];
            try { gcm.Decrypt(Nonce(baseNonce, i), data.AsSpan(pos, len), data.AsSpan(pos + len, 16), buf, Aad(i, last)); }
            catch (CryptographicException) { throw new DomainException("Nie można odszyfrować załącznika — nieprawidłowy klucz albo plik zmieniony."); }
            plain.Write(buf);
            pos += len + 16;
            i++;
        }
        return plain.ToArray();
    }

    private static byte[] Nonce(byte[] baseNonce, int index)
    {
        var n = (byte[])baseNonce.Clone();
        var c = BitConverter.GetBytes(index);
        for (var k = 0; k < 4; k++) n[8 + k] ^= c[k];
        return n;
    }

    private static byte[] Aad(int index, bool last) => [.. Magic, .. BitConverter.GetBytes(index), (byte)(last ? 1 : 0)];
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
