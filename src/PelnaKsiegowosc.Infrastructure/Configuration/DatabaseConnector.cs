using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Infrastructure.Configuration;

public sealed record ConnectionTestResult(bool Success, string Message, string? ServerVersion, int PendingMigrations, bool DatabaseEmpty);

/// <summary>
/// Łączenie z aktywnym profilem bazy. Brak serwera NIE powoduje przełączenia na inną bazę — użytkownik dostaje
/// komunikat i świadomie wybiera: ponowienie, praca na osobnej kopii roboczej trybu awaryjnego albo zakończenie.
/// </summary>
public sealed class DatabaseConnector
{
    private readonly ISecretStore _secrets;

    public DatabaseConnector(ISecretStore secrets) => _secrets = secrets;

    public string ConnectionString(DatabaseProfile p) => p.Engine == DatabaseEngine.PostgreSql
        ? p.BuildConnectionString(_secrets.Get(p.PasswordSecretKey))
        : p.BuildConnectionString(null, encrypted: SqliteKey(p) is not null);

    public KsiegiDbContext CreateContext(DatabaseProfile p)
    {
        if (p.Engine == DatabaseEngine.PostgreSql) return DbContextFactory.Create(p.Engine, ConnectionString(p));
        EnsureSqliteEncrypted(p);
        return DbContextFactory.Create(p.Engine, ConnectionString(p), SqliteKey(p));
    }

    /// <summary>Plik bazy SQLite jest zaszyfrowany, a na tym stanowisku brak jego klucza (np. drugie stanowisko, inny komputer).</summary>
    public bool SqliteKeyMissing(DatabaseProfile p) =>
        p.Engine == DatabaseEngine.Sqlite && !string.IsNullOrWhiteSpace(p.SqlitePath) && SqliteEncryption.IsEncrypted(Path.GetFullPath(p.SqlitePath)) && SqliteKey(p) is null;

    /// <summary>Klucz dla nowego pliku bazy (np. odtwarzanej z kopii) — istniejący albo nowy, zapisany w magazynie sekretów.</summary>
    public string SqliteKeyForNewFile(DatabaseProfile p)
    {
        var key = SqliteKey(p);
        if (key is not null) return key;
        key = SqliteEncryption.NewKey();
        _secrets.Set(p.PasswordSecretKey, key);
        return key;
    }

    /// <summary>Klucz bazy do przeniesienia na inne stanowisko (wyświetla wyłącznie administrator).</summary>
    public string? ExportSqliteKey(DatabaseProfile p) => p.Engine == DatabaseEngine.Sqlite ? SqliteKey(p) : null;

    /// <summary>Zapis klucza bazy przeniesionego z innego stanowiska — po sprawdzeniu, że otwiera plik.</summary>
    public void ImportSqliteKey(DatabaseProfile p, string key)
    {
        var k = new string((key ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (k.Length != 64) throw new DomainException("Klucz bazy ma 64 znaki szesnastkowe.");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(p.BuildConnectionString(null, encrypted: true)))
        {
            c.Open();
            try { SqliteEncryption.ApplyKey(c, k); }
            catch (Microsoft.Data.Sqlite.SqliteException) { throw new DomainException("Klucz nie pasuje do tego pliku bazy."); }
        }
        _secrets.Set(p.PasswordSecretKey, k);
    }

    private string? SqliteKey(DatabaseProfile p) => _secrets.Get(p.PasswordSecretKey) is { Length: 64 } k ? k : null;

    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// RODO (art. 32): plik bazy SQLite jest zawsze szyfrowany. Nowa baza dostaje losowy klucz w magazynie sekretów systemu;
    /// istniejąca jawna baza jest szyfrowana przy pierwszym otwarciu (eksport, weryfikacja, podmiana pliku).
    /// Zaszyfrowany plik bez klucza na tym stanowisku wymaga odtworzenia z kopii zapasowej (klucz odzyskiwania kopii).
    /// </summary>
    public void EnsureSqliteEncrypted(DatabaseProfile p)
    {
        if (p.Engine != DatabaseEngine.Sqlite || string.IsNullOrWhiteSpace(p.SqlitePath)) return;
        var path = Path.GetFullPath(p.SqlitePath);
        lock (_checked)
        {
            if (_checked.Contains(path) && File.Exists(path)) return;
            var key = SqliteKey(p);
            if (SqliteEncryption.IsPlaintext(path))
            {
                key ??= SqliteEncryption.NewKey();
                _secrets.Set(p.PasswordSecretKey, key);
                SqliteEncryption.EncryptInPlace(path, key);
            }
            else if (SqliteEncryption.IsEncrypted(path) && key is null)
                throw new InvalidOperationException("Plik bazy jest zaszyfrowany, a na tym stanowisku nie ma klucza bazy. Odtwórz bazę z kopii zapasowej (klucz odzyskiwania kopii) w kreatorze lub sekcji „Backupy”.");
            else if (key is null)
                _secrets.Set(p.PasswordSecretKey, SqliteEncryption.NewKey()); // nowa baza — powstanie od razu zaszyfrowana
            if (File.Exists(path)) _checked.Add(path);
        }
    }

    public void SavePassword(DatabaseProfile p, string password) => _secrets.Set(p.PasswordSecretKey, password);

    public async Task<ConnectionTestResult> TestAsync(DatabaseProfile p, CancellationToken ct = default)
    {
        try
        {
            await using var db = CreateContext(p);
            var conn = db.Database.GetDbConnection();
            if (p.Engine == DatabaseEngine.Sqlite) await db.Database.OpenConnectionAsync(ct); // przez EF — ustawia klucz szyfrowania SQLite
            else await conn.OpenAsync(ct);
            var version = conn.ServerVersion;
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).Any();
            var empty = !applied || !await db.Companies.AnyAsync(ct);
            if (p.Engine == DatabaseEngine.Sqlite) await db.Database.CloseConnectionAsync(); else await conn.CloseAsync();
            p.LastSuccessfulConnectionUtc = DateTime.UtcNow;
            p.LastConnectionError = null;
            return new ConnectionTestResult(true, $"Połączono ({p.SafeDescription}). Wersja serwera: {version}. Oczekujące migracje: {pending}.", version, pending, empty);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException or IOException)
        {
            var msg = Describe(ex);
            p.LastConnectionError = msg;
            return new ConnectionTestResult(false, msg, null, 0, false);
        }
    }

    /// <summary>Komunikat dla użytkownika bez ujawniania hasła i pełnego łańcucha połączenia.</summary>
    public static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is not PostgresException && inner is not NpgsqlException) inner = inner.InnerException;
        return inner switch
        {
            PostgresException { SqlState: "28P01" } => "Uwierzytelnienie nieudane — nieprawidłowy użytkownik lub hasło.",
            PostgresException { SqlState: "3D000" } => "Baza danych o podanej nazwie nie istnieje. W kroku 2 kreatora wybierz „Utwórz pierwszą bazę” i podaj osobne dane administratora PostgreSQL.",
            PostgresException { SqlState: "42501" } => "Brak wymaganych uprawnień konta PostgreSQL (42501). Administrator musi sprawdzić dostęp operatora do schematu, tabel i sekwencji. Migracje schematu wykonuje osobno konto administracyjne; nie nadawaj operatorowi praw superusera.",
            PostgresException pe => $"Błąd serwera PostgreSQL ({pe.SqlState}).",
            NpgsqlException { InnerException: System.Net.Sockets.SocketException } => "Serwer niedostępny — brak połączenia sieciowego z podanym adresem i portem.",
            NpgsqlException { InnerException: TimeoutException } or TimeoutException => "Przekroczono limit czasu połączenia z serwerem.",
            NpgsqlException ne when ne.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) => "Błąd TLS — sprawdź tryb szyfrowania i certyfikat CA.",
            NpgsqlException => "Nie udało się połączyć z serwerem PostgreSQL.",
            _ => "Błąd połączenia z bazą: " + Sanitize(inner.Message)
        };
    }

    private static string Sanitize(string s)
    {
        var i = s.IndexOf("Password", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? s[..i] + "[ukryto]" : s;
    }

    /// <summary>
    /// Starszy punkt wejścia do otwierania aktywnych ksiąg. Tak samo jak AppSession.OpenAsync
    /// nie może samodzielnie przeprowadzać migracji schematu PostgreSQL. SQLite może być bazą główną.
    /// Migracje PostgreSQL wykonuje administrator w osobnej operacji z kopią bezpieczeństwa.
    /// </summary>
    public async Task<(KsiegiDbContext? db, string? error)> OpenActiveAsync(DatabaseProfile p, CancellationToken ct = default)
    {
        if (p.Engine == DatabaseEngine.Sqlite && !string.IsNullOrWhiteSpace(p.SqlitePath))
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p.SqlitePath))!);
        var test = await TestAsync(p, ct);
        if (!test.Success)
            return (null, test.Message + " Program nie przełącza się automatycznie na bazę lokalną.");
        if (test.PendingMigrations > 0)
        {
            if (p.Engine == DatabaseEngine.Sqlite && test.DatabaseEmpty)
            {
                var sqlite = CreateContext(p);
                try { await sqlite.Database.MigrateAsync(ct); return (sqlite, null); }
                catch { await sqlite.DisposeAsync(); throw; }
            }
            if (p.Engine == DatabaseEngine.Sqlite)
                return (null, "Baza SQLite wymaga kopii bezpieczeństwa i aktualizacji. Otwórz ją przez skonfigurowane stanowisko albo zaimportuj do nowej bazy.");
            return (null, $"Wymagana aktualizacja schematu PostgreSQL ({test.PendingMigrations} oczekujących migracji). Administrator musi wykonać ją osobno, po sprawdzeniu kopii zapasowej i zakończeniu pozostałych sesji. Program nie aktualizuje schematu automatycznie.");
        }
        return (CreateContext(p), null);
    }

    /// <summary>
    /// Tryb awaryjny: utworzenie OSOBNEJ, jawnie oznaczonej kopii roboczej SQLite z ostatniej kopii zapasowej serwera.
    /// Zmiany w kopii roboczej wymagają późniejszego uzgodnienia (import dokumentów) z bazą główną.
    /// </summary>
    public static DatabaseProfile EmergencyProfile(AppPaths paths, DateTime nowUtc) => new()
    {
        Name = $"KOPIA ROBOCZA TRYBU AWARYJNEGO {SystemClock.ToWarsaw(nowUtc):yyyy-MM-dd HH:mm}",
        Engine = DatabaseEngine.Sqlite,
        SqlitePath = Path.Combine(paths.DataDirectory, $"awaryjna-{nowUtc:yyyyMMddHHmmss}", "ksiegi.db"),
        IsEmergencyWorkingCopy = true
    };
}
