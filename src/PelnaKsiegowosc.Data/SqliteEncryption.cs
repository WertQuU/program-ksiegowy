using System.Data.Common;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PelnaKsiegowosc.Data;

/// <summary>
/// Szyfrowanie pliku bazy SQLite w spoczynku (SQLCipher 4, AES-256, HMAC stron). Klucz to 256 losowych bitów
/// przechowywanych w magazynie sekretów systemu (DPAPI / Windows Credential Manager, libsecret) — nigdy w pliku
/// konfiguracji ani w repozytorium. Klucz jest podawany jako surowy klucz (bez KDF), dlatego otwarcie połączenia jest szybkie.
/// Kopie zapasowe zawierają bazę w postaci jawnej wewnątrz archiwum szyfrowanego kluczem kopii (AES-256-GCM), więc
/// do odtworzenia wystarcza klucz odzyskiwania kopii.
/// </summary>
public static class SqliteEncryption
{
    private static readonly byte[] PlainHeader = "SQLite format 3\0"u8.ToArray();

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Plik istnieje i jest jawną bazą SQLite (nagłówek „SQLite format 3”).</summary>
    public static bool IsPlaintext(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < PlainHeader.Length) return false;
        Span<byte> head = stackalloc byte[16];
        using var f = File.OpenRead(path);
        return f.Read(head) == 16 && head.SequenceEqual(PlainHeader);
    }

    public static bool IsEncrypted(string path) => File.Exists(path) && new FileInfo(path).Length > 0 && !IsPlaintext(path);

    /// <summary>Ustawienie klucza na świeżo otwartym połączeniu (pierwsza instrukcja) oraz włączenie kluczy obcych.</summary>
    public static void ApplyKey(DbConnection connection, string hexKey)
    {
        if (hexKey.Length != 64 || !hexKey.All(Uri.IsHexDigit)) throw new InvalidOperationException("Nieprawidłowy klucz szyfrowania bazy.");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA key = \"x'{hexKey}'\"; PRAGMA foreign_keys = ON; SELECT count(*) FROM sqlite_master;";
        cmd.ExecuteScalar();
    }

    /// <summary>
    /// Zaszyfrowanie istniejącej jawnej bazy: eksport do nowego pliku, pełna kontrola integralności
    /// i atomowa zamiana w tym samym katalogu. Nie nadpisujemy/nie usuwamy oryginału przed
    /// zatwierdzeniem zaszyfrowanej kopii. Szyfrowanie całego nośnika pozostaje zalecane
    /// (operacja nie gwarantuje fizycznego usunięcia historycznych bloków SSD).
    /// </summary>
    public static void EncryptInPlace(string path, string hexKey)
    {
        if (!IsPlaintext(path)) throw new InvalidOperationException("Plik nie jest jawną bazą SQLite.");
        if (hexKey.Length != 64 || !hexKey.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Nieprawidłowy klucz szyfrowania bazy.");
        var tmp = path + ".szyfrowanie";
        // Pozostałość po przerwanym szyfrowaniu może zawierać poprawną, jeszcze nieprzełączoną
        // bazę. Nie kasujemy jej automatycznie i nie ryzykujemy utraty danych.
        if (File.Exists(tmp))
            throw new InvalidOperationException("Wykryto plik pozostały po przerwanym szyfrowaniu. Sprawdź i zabezpiecz plik .szyfrowanie przed ponowną próbą.");
        try
        {
            using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                src.Open();
                using var cmd = src.CreateCommand();
                cmd.CommandText = $"ATTACH DATABASE {Quote(tmp)} AS zaszyfrowana KEY \"x'{hexKey}'\"; SELECT sqlcipher_export('zaszyfrowana'); DETACH DATABASE zaszyfrowana;";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            VerifyEncryptedCopy(path, tmp, hexKey);
            // Systemowa operacja replace gwarantuje brak etapu bez pliku docelowego.
            // Source i destination są w tym samym katalogu/woluminie.
            File.Replace(tmp, path, destinationBackupFileName: null);
            foreach (var side in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(path + side)) File.Delete(path + side);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            // Nie usuwać pliku docelowego ani potencjalnego odzyskiwalnego eksportu.
            throw;
        }
    }

    /// <summary>Zapis jawnej bazy roboczej bezpośrednio jako zaszyfrowanego pliku docelowego (bez jawnej kopii w miejscu docelowym).</summary>
    public static void ExportEncrypted(string plainSource, string encryptedTarget, string hexKey)
    {
        if (File.Exists(encryptedTarget)) throw new InvalidOperationException("Plik docelowy już istnieje.");
        using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = plainSource, Pooling = false }.ToString()))
        {
            src.Open();
            using var cmd = src.CreateCommand();
            cmd.CommandText = $"ATTACH DATABASE {Quote(encryptedTarget)} AS zaszyfrowana KEY \"x'{hexKey}'\"; SELECT sqlcipher_export('zaszyfrowana'); DETACH DATABASE zaszyfrowana;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        try
        {
            VerifyEncryptedCopy(plainSource, encryptedTarget, hexKey);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(encryptedTarget)) File.Delete(encryptedTarget);
            throw;
        }
    }

    /// <summary>
    /// Pełna weryfikacja zaszyfrowanej kopii: plik nie jest jawny, otwiera się wyłącznie podanym kluczem, przechodzi kontrolę
    /// HMAC stron SQLCipher (cipher_integrity_check) i strukturalną (integrity_check), a schemat i liczby wierszy każdej tabeli
    /// oraz suma kontrolna dziennika są identyczne jak w źródle.
    /// </summary>
    public static void VerifyEncryptedCopy(string plainSource, string encryptedTarget, string hexKey)
    {
        if (!IsEncrypted(encryptedTarget)) throw new InvalidOperationException("Zapis zaszyfrowanej bazy nie powiódł się — plik nie jest zaszyfrowany.");
        Dictionary<string, string> expected;
        using (var a = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = plainSource, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            a.Open();
            expected = Fingerprint(a);
        }
        using (var b = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = encryptedTarget, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            b.Open();
            ApplyKey(b, hexKey);
            using (var cmd = b.CreateCommand())
            {
                cmd.CommandText = "PRAGMA cipher_integrity_check";
                using var r = cmd.ExecuteReader();
                if (r.Read()) throw new InvalidOperationException("Kontrola integralności stron zaszyfrowanej bazy wykazała błąd: " + r.GetValue(0));
            }
            using (var cmd = b.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check";
                var res = Convert.ToString(cmd.ExecuteScalar());
                if (res != "ok") throw new InvalidOperationException("Kontrola integralności zaszyfrowanej bazy: " + res);
            }
            var actual = Fingerprint(b);
            var diff = expected.Where(kv => !actual.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key)
                .Concat(actual.Keys.Except(expected.Keys)).ToList();
            if (diff.Count > 0) throw new InvalidOperationException("Zaszyfrowana kopia różni się od źródła: " + string.Join(", ", diff.Take(10)));
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Odcisk bazy: definicja każdego obiektu schematu, liczba wierszy każdej tabeli i skrót SHA-256 tabeli zapisów dziennika.</summary>
    private static Dictionary<string, string> Fingerprint(DbConnection c)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, coalesce(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result["schemat:" + r.GetString(0) + ":" + r.GetString(1)] = r.GetString(2);
                if (r.GetString(0) == "table") tables.Add(r.GetString(1));
            }
        }
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM \"{t.Replace("\"", "\"\"")}\"";
            result["wiersze:" + t] = Convert.ToString(cmd.ExecuteScalar())!;
        }
        foreach (var t in new[] { "JournalEntries", "JournalLines" }.Where(tables.Contains))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\" ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                {
                    var v = r.IsDBNull(i) ? "\u0000" : r.GetValue(i) is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
                    sha.AppendData(System.Text.Encoding.UTF8.GetBytes(v + "\u001f"));
                }
            result["skrot:" + t] = Convert.ToHexString(sha.GetHashAndReset());
        }
        return result;
    }

    /// <summary>Eksport bieżącej bazy (zaszyfrowanej lub jawnej) do jawnego pliku — wyłącznie do archiwum kopii szyfrowanego kluczem kopii.</summary>
    public static void ExportPlaintext(DbConnection openSource, string destinationPath)
    {
        using var cmd = openSource.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE {Quote(destinationPath)} AS jawna KEY ''; SELECT sqlcipher_export('jawna'); DETACH DATABASE jawna;";
        cmd.ExecuteNonQuery();
    }

    private static long CountObjects(DbConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master";
        var tables = Convert.ToInt64(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='JournalEntries'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return tables;
        cmd.CommandText = "SELECT count(*) FROM JournalEntries";
        return tables * 1_000_000 + Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}

/// <summary>Ustawia klucz SQLCipher przy każdym otwarciu połączenia EF Core.</summary>
public sealed class SqliteKeyInterceptor : DbConnectionInterceptor
{
    private sealed record ContextKey(string Value);
    private static readonly ConditionalWeakTable<DbContext, ContextKey> ContextKeys = new();

    // A shared stateless instance is used by DbContextFactory.Create. The weak
    // association prevents keys from being kept alive after their contexts are collected.
    public static SqliteKeyInterceptor Shared { get; } = new();
    private readonly string? _hexKey;
    private SqliteKeyInterceptor() { }

    // Preserve the existing direct BuildOptions(..., sqliteKey) contract.
    public SqliteKeyInterceptor(string hexKey) => _hexKey = hexKey;

    public static void Register(DbContext context, string hexKey) =>
        ContextKeys.Add(context, new ContextKey(hexKey));

    private string KeyFor(ConnectionEndEventData eventData)
    {
        if (_hexKey is not null) return _hexKey;
        if (eventData.Context is not null && ContextKeys.TryGetValue(eventData.Context, out var key))
            return key.Value;
        throw new InvalidOperationException("Brak klucza szyfrowania przypisanego do kontekstu SQLite.");
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        SqliteEncryption.ApplyKey(connection, KeyFor(eventData));

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        SqliteEncryption.ApplyKey(connection, KeyFor(eventData));
        return Task.CompletedTask;
    }
}
