using System.Security.Cryptography;
using System.Text;

namespace PelnaKsiegowosc.Data;

/// <summary>
/// Deterministyczne utrudnienie bezpośredniego odczytu i edycji wybranych kolumn PostgreSQL.
/// Klucz pochodzi wyłącznie ze stałego identyfikatora firmy (CompanyId), wspólnego na wszystkich
/// stanowiskach połączonych z tym samym serwerem. Żadnych losowych lub użytkownikowych kluczy.
/// UWAGA: CompanyId jest dostępny w bazie; to NIE jest ochrona przed świadomym atakującym
/// znającym algorytm. Tag AES-GCM wykrywa przypadkowe zmiany, lecz nie dowodzi autentyczności
/// danych wobec administratora SQL, który może sam odtworzyć klucz.
/// </summary>
public static class CompanyFieldCodec
{
    public const string Prefix = "PK1$";
    private const string Purpose = "PelnaKsiegowosc/PostgresFields/v1/";

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    private static byte[] Key(Guid companyId, string label)
    {
        if (companyId == Guid.Empty) throw new InvalidDataException("Nie można zabezpieczyć pola bez identyfikatora firmy.");
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(Purpose + companyId.ToString("N")));
        return HMACSHA256.HashData(seed, Encoding.UTF8.GetBytes(label));
    }

    private static byte[] Context(Guid companyId, Guid recordId, string field)
    {
        if (recordId == Guid.Empty || string.IsNullOrWhiteSpace(field))
            throw new InvalidDataException("Brak identyfikatora rekordu lub nazwy pola.");
        return Encoding.UTF8.GetBytes(Purpose + companyId.ToString("N") + "/" + recordId.ToString("N") + "/" + field);
    }

    /// <summary>
    /// Deterministyczny nonce wyliczany jako HMAC(AAD || długość || dane).
    /// Dla danego rekordu, pola i tekstu wynik jest zawsze identyczny.
    /// To nie jest osobny losowy klucz, a ponowne zapisanie tej samej wartości
    /// nie zużywa nowego nonce dla innego plaintextu.
    /// </summary>
    public static string? Protect(string? plaintext, Guid companyId, Guid recordId, string field)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (IsProtected(plaintext))
            throw new InvalidDataException("Nie zapisuj zaszyfrowanego ciągu jako jawnej wartości pola.");
        var context = Context(companyId, recordId, field);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var key = Key(companyId, "aes");
        var nonceKey = Key(companyId, "nonce");
        var nonceData = new byte[context.Length + 8 + bytes.Length];
        context.CopyTo(nonceData, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(nonceData.AsSpan(context.Length, 8), bytes.Length);
        bytes.CopyTo(nonceData, context.Length + 8);
        var nonce = HMACSHA256.HashData(nonceKey, nonceData).AsSpan(0, 12).ToArray();
        var encrypted = new byte[bytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, bytes, encrypted, tag, context);
        var packed = new byte[nonce.Length + tag.Length + encrypted.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        encrypted.CopyTo(packed, nonce.Length + tag.Length);
        return Prefix + Convert.ToBase64String(packed);
    }

    public static string? Unprotect(string? value, Guid companyId, Guid recordId, string field)
    {
        if (string.IsNullOrEmpty(value) || !IsProtected(value)) return value; // stare rekordy przed migracją
        try
        {
            var context = Context(companyId, recordId, field);
            var bytes = Convert.FromBase64String(value[Prefix.Length..]);
            if (bytes.Length < 29) throw new CryptographicException("Zbyt krótka wartość.");
            var plain = new byte[bytes.Length - 28];
            using (var aes = new AesGcm(Key(companyId, "aes"), 16))
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, context);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Wykryto uszkodzoną lub zmodyfikowaną wartość chronionego pola PostgreSQL: " + field, ex);
        }
    }
}
