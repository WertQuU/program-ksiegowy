using System.Security.Cryptography;
using System.Text;
using PsaKsiegi.Domain.Common;

namespace PsaKsiegi.Application.Security;

/// <summary>Hashowanie haseł: PBKDF2-HMAC-SHA256, 600 000 iteracji (rekomendacja OWASP 2023), sól 16 B, skrót 32 B.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 600_000;

    public static (string hash, string salt, int iterations) Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hashB64, string saltB64, int iterations)
    {
        var expected = Convert.FromBase64String(hashB64);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(saltB64), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Polityka haseł: min. 12 znaków, co najmniej 3 z 4 klas znaków, bez loginu w haśle.</summary>
    public static ValidationResult ValidatePolicy(string password, string login)
    {
        var r = new ValidationResult();
        if (password.Length < 12) r.Error("Hasło musi mieć co najmniej 12 znaków.");
        var classes = new[] { password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) }.Count(x => x);
        if (classes < 3) r.Error("Hasło musi zawierać znaki z co najmniej 3 grup: małe litery, wielkie litery, cyfry, znaki specjalne.");
        if (!string.IsNullOrWhiteSpace(login) && password.Contains(login, StringComparison.OrdinalIgnoreCase)) r.Error("Hasło nie może zawierać loginu.");
        return r;
    }
}

/// <summary>TOTP (RFC 6238, HMAC-SHA1, 30 s, 6 cyfr) — zgodny z popularnymi aplikacjami uwierzytelniającymi.</summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Compute(byte[] secret, long unixSeconds, int digits = 6, int step = 30)
    {
        var counter = unixSeconds / step;
        var cb = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(cb);
        using var h = new HMACSHA1(secret);
        var hash = h.ComputeHash(cb);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var mod = (int)Math.Pow(10, digits);
        return (bin % mod).ToString().PadLeft(digits, '0');
    }

    /// <summary>Weryfikacja z tolerancją ±1 krok (rozbieżność zegarów).</summary>
    public static bool Verify(byte[] secret, string code, DateTime utcNow, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsDigit)) return false;
        var now = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
        for (var i = -window; i <= window; i++)
        {
            var c = Compute(secret, now + i * 30);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c), Encoding.ASCII.GetBytes(code))) return true;
        }
        return false;
    }

    public static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string s)
    {
        var clean = s.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Nieprawidłowy znak Base32.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    public static string ProvisioningUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
}

/// <summary>
/// Szyfrowanie wartości przechowywanych w bazie (np. sekret MFA) — AES-256-GCM, klucz bazy przechowywany
/// w magazynie sekretów systemu (nie w bazie). Format: v1:base64(nonce|tag|ciphertext).
/// </summary>
public sealed class DataProtector
{
    private readonly byte[] _key;

    public DataProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Klucz musi mieć 256 bitów.");
        _key = key;
    }

    public string Protect(byte[] plaintext, string purpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ct = new byte[plaintext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ct, tag, Encoding.UTF8.GetBytes(purpose));
        return "v1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(ct).ToArray());
    }

    public byte[] Unprotect(string protectedValue, string purpose)
    {
        if (!protectedValue.StartsWith("v1:")) throw new CryptographicException("Nieznany format zaszyfrowanej wartości.");
        var raw = Convert.FromBase64String(protectedValue[3..]);
        var nonce = raw[..12];
        var tag = raw[12..28];
        var ct = raw[28..];
        var pt = new byte[ct.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ct, tag, pt, Encoding.UTF8.GetBytes(purpose));
        return pt;
    }

    public static string KeySecretName(Guid databaseId) => $"PsaKsiegi/dbkey/{databaseId:N}";

    public static DataProtector ForDatabase(Abstractions.ISecretStore store, Guid databaseId, bool createIfMissing)
    {
        var name = KeySecretName(databaseId);
        var existing = store.Get(name);
        if (existing is null)
        {
            if (!createIfMissing)
                throw new DomainException("Brak klucza szyfrowania bazy na tym komputerze. Zaimportuj klucz (Ustawienia → Bezpieczeństwo) lub użyj pliku odzyskiwania klucza.");
            var key = RandomNumberGenerator.GetBytes(32);
            store.Set(name, Convert.ToBase64String(key));
            return new DataProtector(key);
        }
        return new DataProtector(Convert.FromBase64String(existing));
    }
}
