using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
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

    /// <summary>Zaszyfrowanie istniejącej jawnej bazy: eksport do nowego pliku zaszyfrowanego, weryfikacja, podmiana pliku.</summary>
    public static void EncryptInPlace(string path, string hexKey)
    {
        if (!IsPlaintext(path)) throw new InvalidOperationException("Plik nie jest jawną bazą SQLite.");
        var tmp = path + ".szyfrowanie";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            src.Open();
            using var cmd = src.CreateCommand();
            cmd.CommandText = $"ATTACH DATABASE {Quote(tmp)} AS zaszyfrowana KEY \"x'{hexKey}'\"; SELECT sqlcipher_export('zaszyfrowana'); DETACH DATABASE zaszyfrowana;";
            cmd.ExecuteNonQuery();
        }
        long plainRows, encRows;
        using (var a = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            a.Open();
            plainRows = CountObjects(a);
        }
        using (var b = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            b.Open();
            ApplyKey(b, hexKey);
            encRows = CountObjects(b);
        }
        SqliteConnection.ClearAllPools();
        if (plainRows != encRows) { File.Delete(tmp); throw new InvalidOperationException("Weryfikacja zaszyfrowanej bazy nie powiodła się — baza pozostała bez zmian."); }
        // nadpisanie jawnej treści przed usunięciem (na nośnikach SSD nie gwarantuje fizycznego usunięcia — zalecane szyfrowanie dysku)
        var length = new FileInfo(path).Length;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            var zeros = new byte[64 * 1024];
            for (long done = 0; done < length; done += zeros.Length) f.Write(zeros, 0, (int)Math.Min(zeros.Length, length - done));
        }
        File.Delete(path);
        File.Move(tmp, path);
        foreach (var side in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + side)) File.Delete(path + side);
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
        if (!IsEncrypted(encryptedTarget)) throw new InvalidOperationException("Zapis zaszyfrowanej bazy nie powiódł się.");
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
    private readonly string _hexKey;
    public SqliteKeyInterceptor(string hexKey) => _hexKey = hexKey;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => SqliteEncryption.ApplyKey(connection, _hexKey);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        SqliteEncryption.ApplyKey(connection, _hexKey);
        return Task.CompletedTask;
    }
}
