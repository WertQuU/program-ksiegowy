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
/// Znacznik formatu katalogu (<c>.szyfrowanie</c>) zapisuje, że wszystkie pliki magazynu są zaszyfrowane — plik bez nagłówka
/// szyfrowania jest wtedy uszkodzony (a nie „jawny”). Zapis, odczyt i kontrola są strumieniowe (porcje 1 MiB).
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    private const string FormatMarker = ".szyfrowanie";
    private readonly byte[]? _key;
    private readonly bool _keyRequired;

    /// <param name="keyRequired">Magazyn programu: bez klucza (np. klucz nie pasuje do plików w katalogu) zapis jest blokowany,
    /// aby nie powstały pliki jawne ani zaszyfrowane innym kluczem.</param>
    public LocalFileStore(string rootPath, byte[]? key = null, bool keyRequired = false)
    {
        _keyRequired = keyRequired;
        RootPath = Path.GetFullPath(rootPath);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(RootPath);
        else Directory.CreateDirectory(RootPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (key is not null && key.Length != 32) throw new ArgumentException("Klucz załączników musi mieć 256 bitów.", nameof(key));
        _key = key;
        // nowy (pusty) magazyn z kluczem — od początku wyłącznie pliki zaszyfrowane
        if (key is not null && !EncryptedOnly(RootPath) && !Directory.EnumerateFiles(RootPath, "*.bin", SearchOption.AllDirectories).Any())
            MarkEncryptedOnly();
    }

    public string RootPath { get; }
    public bool Encrypted => _key is not null;

    /// <summary>Katalog oznaczony jako zawierający wyłącznie pliki zaszyfrowane.</summary>
    public static bool EncryptedOnly(string rootPath) => File.Exists(Path.Combine(rootPath, FormatMarker));

    private void MarkEncryptedOnly() => File.WriteAllText(Path.Combine(RootPath, FormatMarker), "PKZ1");

    public async Task<(string storagePath, string sha256, long size)> SaveAsync(Stream content, string suggestedName, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rel = Path.Combine(now.Year.ToString("0000"), now.Month.ToString("00"), Guid.NewGuid().ToString("N") + ".bin").Replace('\\', '/');
        var (sha, size) = await SaveAtAsync(rel, content, ct);
        return (rel, sha, size);
    }

    /// <summary>Zapis pod wskazaną ścieżką magazynu (odtwarzanie kopii, migracja baz) — strumieniowo, z szyfrowaniem, jeśli magazyn ma klucz.</summary>
    public async Task<(string sha256, long size)> SaveAtAsync(string storagePath, Stream content, CancellationToken ct = default)
    {
        var full = Resolve(storagePath);
        if (_key is null && (_keyRequired || EncryptedOnly(RootPath)))
            throw new DomainException("Na tym stanowisku brak klucza załączników pasującego do plików w katalogu załączników — zaimportuj klucz (Ustawienia → klucz załączników).");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        (string, long) result;
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                result = _key is null ? await AttachmentCrypto.CopyWithHashAsync(content, fs, ct) : await AttachmentCrypto.EncryptStreamAsync(content, fs, _key, ct);
                await fs.FlushAsync(ct);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
        return result;
    }

    /// <summary>
    /// Odczyt strumieniowy: plik zaszyfrowany jest najpierw w całości uwierzytelniany (przebieg bez zapisu treści), a dopiero potem
    /// udostępniany do odczytu porcjami — zmieniony lub obcięty plik nie zostanie częściowo wydany. Pamięć: jedna porcja (1 MiB).
    /// </summary>
    public async Task<Stream> OpenAsync(string storagePath, CancellationToken ct = default)
    {
        var full = Resolve(storagePath);
        if (_key is not null && HasEncryptedHeader(full))
            await using (var verify = new AttachmentCrypto.DecryptingStream(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true), _key))
                await verify.CopyToAsync(Stream.Null, ct);
        return OpenUnverified(full, storagePath);
    }

    private Stream OpenUnverified(string full, string storagePath)
    {
        var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        try
        {
            var head = new byte[4];
            var n = fs.ReadAtLeast(head, 4, throwOnEndOfStream: false);
            fs.Position = 0;
            if (n == 4 && AttachmentCrypto.IsEncrypted(head))
            {
                if (_key is null) throw new DomainException("Załącznik jest zaszyfrowany, a na tym stanowisku brak klucza załączników.");
                return new AttachmentCrypto.DecryptingStream(fs, _key);
            }
            if (EncryptedOnly(RootPath))
                throw new DomainException($"Załącznik {storagePath} jest uszkodzony — brak nagłówka szyfrowania w magazynie zawierającym wyłącznie pliki zaszyfrowane.");
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default) => Task.FromResult(File.Exists(Resolve(storagePath)));

    /// <summary>
    /// Szyfruje pliki zapisane wcześniej jawnie (zachowując ścieżki i sumy SHA-256 treści), po czym oznacza katalog jako
    /// zawierający wyłącznie pliki zaszyfrowane. Gdy katalog jest już oznaczony — nic nie robi (plik bez nagłówka jest wtedy
    /// uszkodzony i nie może zostać „zaszyfrowany” jako jawny). Zwraca liczbę zaszyfrowanych plików.
    /// </summary>
    public async Task<int> EncryptLegacyFilesAsync(CancellationToken ct = default)
    {
        if (_key is null || !Directory.Exists(RootPath) || EncryptedOnly(RootPath)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(RootPath, "*.bin", SearchOption.AllDirectories).ToList())
        {
            if (HasEncryptedHeader(f)) continue;
            var tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await AttachmentCrypto.EncryptStreamAsync(src, fs, _key, ct);
            File.Move(tmp, f, overwrite: true);
            n++;
        }
        MarkEncryptedOnly();
        return n;
    }

    private static bool HasEncryptedHeader(string file)
    {
        using var fs = File.OpenRead(file);
        var head = new byte[4];
        return fs.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && AttachmentCrypto.IsEncrypted(head);
    }

    /// <summary>Wynik kontroli magazynu: pliki otwierane kluczem, pliki jawne (do zaszyfrowania), pliki nieczytelne (inny klucz lub uszkodzenie).</summary>
    public sealed record StoreCheck(int Encrypted, int Plain, IReadOnlyList<string> Unreadable)
    {
        public bool Ok => Unreadable.Count == 0;
    }

    /// <summary>
    /// Pełna, strumieniowa kontrola magazynu: każdy zaszyfrowany załącznik musi dać się odszyfrować i uwierzytelnić podanym kluczem
    /// (pliki zaszyfrowane innym kluczem, obcięte lub zmienione); w katalogu oznaczonym jako zaszyfrowany plik bez nagłówka
    /// szyfrowania jest uszkodzony.
    /// </summary>
    public static StoreCheck CheckStore(string rootPath, byte[] key)
    {
        var unreadable = new List<string>();
        int enc = 0, plain = 0;
        if (!Directory.Exists(rootPath)) return new StoreCheck(0, 0, unreadable);
        var encryptedOnly = EncryptedOnly(rootPath);
        foreach (var f in Directory.EnumerateFiles(rootPath, "*.bin", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
            if (!HasEncryptedHeader(f))
            {
                if (encryptedOnly) unreadable.Add(rel); else plain++;
                continue;
            }
            try
            {
                using var ds = new AttachmentCrypto.DecryptingStream(File.OpenRead(f), key);
                ds.CopyTo(Stream.Null);
                enc++;
            }
            catch (Exception e) when (e is CryptographicException or DomainException) { unreadable.Add(rel); }
        }
        return new StoreCheck(enc, plain, unreadable);
    }

    /// <summary>Czy klucz otwiera wszystkie załączniki zaszyfrowane w katalogu; pusty katalog — tak.</summary>
    public static bool KeyMatches(string rootPath, byte[] key) => CheckStore(rootPath, key).Ok;

    /// <summary>Pełna kontrola magazynu tym kluczem (przed migracją, zmianą klucza, po imporcie klucza).</summary>
    public StoreCheck Check() => _key is null
        ? throw new DomainException("Magazyn nie ma klucza załączników.")
        : CheckStore(RootPath, _key);

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
/// porcji; dane dodatkowe: numer porcji i znacznik ostatniej porcji — wykrycie zamiany kolejności i obcięcia pliku). Porcje
/// pośrednie są zawsze pełne; plik musi zawierać co najmniej jedną porcję (pusty załącznik — jedna porcja o długości 0).
/// </summary>
public static class AttachmentCrypto
{
    private static readonly byte[] Magic = "PKZ1"u8.ToArray();
    internal const int Chunk = 1024 * 1024;
    private const int HeaderLength = 16;

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    /// <summary>Kopia strumienia z sumą SHA-256 (magazyn bez klucza).</summary>
    public static async Task<(string sha256, long size)> CopyWithHashAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[81920];
        long size = 0;
        int n;
        while ((n = await input.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            await output.WriteAsync(buf.AsMemory(0, n), ct);
            size += n;
        }
        return (Convert.ToHexString(sha.GetHashAndReset()), size);
    }

    /// <summary>Szyfrowanie strumieniowe (pamięć: dwie porcje) z sumą SHA-256 treści jawnej.</summary>
    public static async Task<(string sha256, long size)> EncryptStreamAsync(Stream input, Stream output, byte[] key, CancellationToken ct = default)
    {
        var baseNonce = RandomNumberGenerator.GetBytes(12);
        await output.WriteAsync(Magic, ct);
        await output.WriteAsync(baseNonce, ct);
        using var gcm = new AesGcm(key, 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var current = new byte[Chunk];
        var next = new byte[Chunk];
        var cipher = new byte[Chunk];
        var tag = new byte[16];
        var curLen = await input.ReadAtLeastAsync(current, Chunk, throwOnEndOfStream: false, ct);
        long size = 0;
        for (var i = 0; ; i++)
        {
            // odczyt z wyprzedzeniem — porcja jest ostatnia, gdy po niej nie ma już danych
            var nextLen = curLen == Chunk ? await input.ReadAtLeastAsync(next, Chunk, throwOnEndOfStream: false, ct) : 0;
            var last = nextLen == 0;
            sha.AppendData(current, 0, curLen);
            size += curLen;
            gcm.Encrypt(Nonce(baseNonce, i), current.AsSpan(0, curLen), cipher.AsSpan(0, curLen), tag, Aad(i, last));
            await output.WriteAsync(BitConverter.GetBytes(curLen), ct);
            await output.WriteAsync(cipher.AsMemory(0, curLen), ct);
            await output.WriteAsync(tag, ct);
            if (last) break;
            (current, next) = (next, current);
            curLen = nextLen;
        }
        return (Convert.ToHexString(sha.GetHashAndReset()), size);
    }

    public static async Task EncryptAsync(byte[] plain, Stream output, byte[] key, CancellationToken ct = default) =>
        await EncryptStreamAsync(new MemoryStream(plain, writable: false), output, key, ct);

    public static byte[] Decrypt(byte[] data, byte[] key)
    {
        using var ds = new DecryptingStream(new MemoryStream(data, writable: false), key);
        using var plain = new MemoryStream();
        ds.CopyTo(plain);
        return plain.ToArray();
    }

    /// <summary>Strumień odszyfrowujący i uwierzytelniający kolejne porcje (wymaga strumienia źródłowego z możliwością ustalenia długości).</summary>
    public sealed class DecryptingStream : Stream
    {
        private readonly Stream _source;
        private readonly AesGcm _gcm;
        private readonly byte[] _baseNonce = new byte[12];
        private readonly long _length;
        private byte[] _buffer = [];
        private int _bufferPos, _bufferLen, _index;
        private bool _finished;

        public DecryptingStream(Stream source, byte[] key)
        {
            _source = source;
            _length = source.Length;
            var head = new byte[HeaderLength];
            if (source.ReadAtLeast(head, HeaderLength, throwOnEndOfStream: false) < HeaderLength || !IsEncrypted(head))
            {
                source.Dispose();
                throw new DomainException("Nieprawidłowy format zaszyfrowanego załącznika.");
            }
            Array.Copy(head, 4, _baseNonce, 0, 12);
            _gcm = new AesGcm(key, 16);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (_bufferPos == _bufferLen && !NextChunk()) return 0;
            var n = Math.Min(destination.Length, _bufferLen - _bufferPos);
            _buffer.AsSpan(_bufferPos, n).CopyTo(destination);
            _bufferPos += n;
            return n;
        }

        private bool NextChunk()
        {
            if (_finished) return false;
            var pos = _source.Position;
            if (pos == _length)
            {
                // koniec bez porcji oznaczonej jako ostatnia (np. sam nagłówek)
                throw new DomainException("Załącznik zaszyfrowany jest uszkodzony (brak treści).");
            }
            var lenBytes = new byte[4];
            if (pos + 4 > _length || _source.ReadAtLeast(lenBytes, 4, throwOnEndOfStream: false) < 4)
                throw new DomainException("Załącznik zaszyfrowany jest uszkodzony (obcięty).");
            var len = BitConverter.ToInt32(lenBytes);
            pos += 4;
            if (len < 0 || len > Chunk || pos + len + 16 > _length) throw new DomainException("Załącznik zaszyfrowany jest uszkodzony.");
            var last = pos + len + 16 == _length;
            if (!last && len != Chunk || len == 0 && _index > 0) throw new DomainException("Załącznik zaszyfrowany jest uszkodzony.");
            var data = new byte[len + 16];
            if (_source.ReadAtLeast(data, len + 16, throwOnEndOfStream: false) < len + 16) throw new DomainException("Załącznik zaszyfrowany jest uszkodzony (obcięty).");
            if (_buffer.Length < len) _buffer = new byte[Chunk];
            try { _gcm.Decrypt(Nonce(_baseNonce, _index), data.AsSpan(0, len), data.AsSpan(len, 16), _buffer.AsSpan(0, len), Aad(_index, last)); }
            catch (CryptographicException) { throw new DomainException("Nie można odszyfrować załącznika — nieprawidłowy klucz albo plik zmieniony."); }
            _bufferPos = 0;
            _bufferLen = len;
            _index++;
            _finished = last;
            if (len == 0) return NextChunkAfterEmpty();
            return true;
        }

        private bool NextChunkAfterEmpty() => !_finished && NextChunk();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _source.Dispose(); _gcm.Dispose(); }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
