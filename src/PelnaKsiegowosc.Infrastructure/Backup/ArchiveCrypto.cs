using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PelnaKsiegowosc.Infrastructure.Backup;

/// <summary>Źródło klucza szyfrowania kopii.</summary>
public abstract record BackupKey
{
    /// <summary>Hasło kopii — klucz wyprowadzany PBKDF2-HMAC-SHA256 (600 000 iteracji).</summary>
    public sealed record Passphrase(string Value) : BackupKey;
    /// <summary>Losowy klucz 256-bitowy (np. przechowywany w Menedżerze poświadczeń Windows do kopii automatycznych).</summary>
    public sealed record RawKey(byte[] Key) : BackupKey;
}

/// <summary>
/// Format zaszyfrowanego archiwum „PKBAK1”: nagłówek (JSON, uwierzytelniany jako AAD), następnie fragmenty
/// AES-256-GCM po 1 MiB. Nonce fragmentu = 8 B losowego prefiksu + 4 B licznika; AAD fragmentu zawiera numer
/// i znacznik ostatniego fragmentu — wykrywa zamianę kolejności, usunięcie i obcięcie archiwum.
/// Utrata hasła/klucza oznacza NIEODWRACALNĄ utratę możliwości odczytu kopii.
/// </summary>
public static class ArchiveCrypto
{
    private static readonly byte[] Magic = "PKBAK1\0\0"u8.ToArray();
    public const int ChunkSize = 1024 * 1024;
    public const int Pbkdf2Iterations = 600_000;

    public sealed record Header(int Version, Guid ArchiveId, string KeyMode, string? Salt, int Iterations, string KeyCheck, string NoncePrefix, int ChunkSize, DateTime CreatedUtc);

    public static void ValidatePassphrase(string p)
    {
        if (p.Length < 12) throw new Domain.Common.DomainException("Hasło kopii zapasowej musi mieć co najmniej 12 znaków.");
    }

    private static byte[] DeriveKey(BackupKey key, byte[]? salt, int iterations) => key switch
    {
        BackupKey.Passphrase p => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(p.Value), salt!, iterations, HashAlgorithmName.SHA256, 32),
        BackupKey.RawKey r when r.Key.Length == 32 => r.Key,
        _ => throw new ArgumentException("Nieprawidłowy klucz kopii.")
    };

    private static string KeyCheck(byte[] key) => Convert.ToHexString(HMACSHA256.HashData(key, "PKBAK-keycheck"u8.ToArray()))[..16];

    public static async Task EncryptAsync(Stream input, Stream output, BackupKey key, Guid archiveId, DateTime nowUtc, CancellationToken ct = default)
    {
        byte[]? salt = key is BackupKey.Passphrase ? RandomNumberGenerator.GetBytes(16) : null;
        if (key is BackupKey.Passphrase pp) ValidatePassphrase(pp.Value);
        var k = DeriveKey(key, salt, Pbkdf2Iterations);
        var prefix = RandomNumberGenerator.GetBytes(8);
        var header = new Header(1, archiveId, key is BackupKey.Passphrase ? "passphrase-pbkdf2-sha256" : "raw-256", salt is null ? null : Convert.ToBase64String(salt),
            Pbkdf2Iterations, KeyCheck(k), Convert.ToBase64String(prefix), ChunkSize, nowUtc);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        await output.WriteAsync(Magic, ct);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, headerBytes.Length);
        await output.WriteAsync(len, ct);
        await output.WriteAsync(headerBytes, ct);
        var headerHash = SHA256.HashData(headerBytes);

        using var aes = new AesGcm(k, 16);
        var buffer = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var read = await ReadFullAsync(input, buffer, ct);
        uint index = 0;
        while (true)
        {
            var nextRead = read == ChunkSize ? await ReadFullAsync(input, next, ct) : 0;
            var isLast = nextRead == 0;
            var nonce = Nonce(prefix, index);
            var aad = Aad(headerHash, index, isLast);
            var ctext = new byte[read];
            var tag = new byte[16];
            aes.Encrypt(nonce, buffer.AsSpan(0, read), ctext, tag, aad);
            var clen = new byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(clen, read);
            clen[4] = isLast ? (byte)1 : (byte)0;
            await output.WriteAsync(clen, ct);
            await output.WriteAsync(tag, ct);
            await output.WriteAsync(ctext, ct);
            if (isLast) break;
            (buffer, next) = (next, buffer);
            read = nextRead;
            index++;
        }
        await output.FlushAsync(ct);
    }

    public static async Task<Header> ReadHeaderAsync(Stream input, CancellationToken ct = default)
    {
        var magic = new byte[8];
        if (await ReadFullAsync(input, magic, ct) != 8 || !magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Plik nie jest zaszyfrowanym archiwum kopii (PKBAK1).");
        var len = new byte[4];
        await ReadFullAsync(input, len, ct);
        var hl = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (hl is <= 0 or > 65536) throw new InvalidDataException("Uszkodzony nagłówek archiwum.");
        var hb = new byte[hl];
        if (await ReadFullAsync(input, hb, ct) != hl) throw new InvalidDataException("Uszkodzony nagłówek archiwum.");
        return JsonSerializer.Deserialize<Header>(hb)! with { };
    }

    public static async Task<Header> DecryptAsync(Stream input, Stream output, BackupKey key, CancellationToken ct = default)
    {
        var magic = new byte[8];
        if (await ReadFullAsync(input, magic, ct) != 8 || !magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Plik nie jest zaszyfrowanym archiwum kopii (PKBAK1).");
        var len = new byte[4];
        await ReadFullAsync(input, len, ct);
        var hl = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (hl is <= 0 or > 65536) throw new InvalidDataException("Uszkodzony nagłówek archiwum.");
        var hb = new byte[hl];
        await ReadFullAsync(input, hb, ct);
        var header = JsonSerializer.Deserialize<Header>(hb)!;
        var headerHash = SHA256.HashData(hb);
        var k = DeriveKey(key, header.Salt is null ? null : Convert.FromBase64String(header.Salt), header.Iterations);
        if (KeyCheck(k) != header.KeyCheck) throw new CryptographicException("Nieprawidłowe hasło lub klucz kopii zapasowej.");
        var prefix = Convert.FromBase64String(header.NoncePrefix);
        using var aes = new AesGcm(k, 16);
        uint index = 0;
        var sawLast = false;
        while (true)
        {
            var clen = new byte[5];
            var r = await ReadFullAsync(input, clen, ct);
            if (r == 0) break;
            if (sawLast) throw new CryptographicException("Dane po ostatnim fragmencie archiwum — plik zmodyfikowany.");
            if (r != 5) throw new CryptographicException("Archiwum jest obcięte.");
            var n = BinaryPrimitives.ReadInt32LittleEndian(clen);
            if (n < 0 || n > header.ChunkSize) throw new InvalidDataException("Uszkodzony fragment archiwum.");
            var isLast = clen[4] == 1;
            var tag = new byte[16];
            if (await ReadFullAsync(input, tag, ct) != 16) throw new CryptographicException("Archiwum jest obcięte.");
            var ctext = new byte[n];
            if (await ReadFullAsync(input, ctext, ct) != n) throw new CryptographicException("Archiwum jest obcięte.");
            var plain = new byte[n];
            // flaga „ostatni” jest częścią AAD — jej podmiana powoduje błąd uwierzytelnienia
            aes.Decrypt(Nonce(prefix, index), ctext, tag, plain, Aad(headerHash, index, isLast));
            await output.WriteAsync(plain, ct);
            sawLast = isLast;
            index++;
        }
        if (!sawLast) throw new CryptographicException("Archiwum jest obcięte (brak ostatniego fragmentu).");
        await output.FlushAsync(ct);
        return header;
    }

    private static byte[] Nonce(byte[] prefix, uint index)
    {
        var n = new byte[12];
        prefix.CopyTo(n, 0);
        BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(8), index);
        return n;
    }

    private static byte[] Aad(byte[] headerHash, uint index, bool last)
    {
        var a = new byte[headerHash.Length + 5];
        headerHash.CopyTo(a, 0);
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(headerHash.Length), index);
        a[^1] = last ? (byte)1 : (byte)0;
        return a;
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var r = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    /// <summary>Klucz odzyskiwania w postaci czytelnej (Base32 w grupach) — do wydrukowania i przechowania w sejfie.</summary>
    public static string FormatRecoveryKey(byte[] key)
    {
        var b32 = Application.Security.Totp.ToBase32(key);
        return string.Join("-", Enumerable.Range(0, (b32.Length + 3) / 4).Select(i => b32.Substring(i * 4, Math.Min(4, b32.Length - i * 4))));
    }

    public static byte[] ParseRecoveryKey(string text) => Application.Security.Totp.FromBase32(text.Replace("-", "").Replace(" ", ""));
}
