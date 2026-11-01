using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PelnaKsiegowosc.Data;

public enum DatabaseEngine
{
    Sqlite,
    PostgreSql
}

/// <summary>Wspólny, jednoznaczny format loginów serwera (bez aliasów przez wielkość liter).</summary>
public static class PostgresLoginPolicy
{
    public static bool IsValid(string? login)
    {
        if (string.IsNullOrEmpty(login) || login.Length > 63 || login.StartsWith("pg_", StringComparison.Ordinal))
            return false;
        if (!(login[0] is >= 'a' and <= 'z' or '_')) return false;
        return login.Skip(1).All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }
}

/// <summary>Rola serwera skonfigurowanego przez użytkownika — wybór świadomy, bez automatycznego przełączania.</summary>
public enum ServerRole
{
    /// <summary>Serwer jest głównym (jedynym aktywnym) magazynem ksiąg.</summary>
    GlownyMagazyn,
    /// <summary>Serwer służy wyłącznie jako dodatkowy cel kopii (eksport danych), księgi pozostają lokalnie.</summary>
    CelKopii
}

public enum PgSslMode
{
    Disable,
    Prefer,
    Require,
    VerifyCA,
    VerifyFull
}

/// <summary>
/// Profil połączenia z bazą. Hasło NIE jest częścią profilu — przechowywane w magazynie sekretów systemu
/// (Windows Credential Manager) pod kluczem <see cref="PasswordSecretKey"/>.
/// </summary>
public sealed class DatabaseProfile
{
    public string Name { get; set; } = "Lokalna baza";
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.Sqlite;
    public string? SqlitePath { get; set; }

    public string? Host { get; set; }
    public int Port { get; set; } = 5432;
    public string? Database { get; set; }
    public string? Username { get; set; }
    public PgSslMode SslMode { get; set; } = PgSslMode.VerifyFull;
    public string? RootCertificatePath { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public ServerRole Role { get; set; } = ServerRole.GlownyMagazyn;

    /// <summary>Kopia robocza trybu awaryjnego — jawnie oznaczona, wymaga późniejszego uzgodnienia.</summary>
    public bool IsEmergencyWorkingCopy { get; set; }
    public DateTime? LastSuccessfulConnectionUtc { get; set; }
    public string? LastConnectionError { get; set; }

    public string PasswordSecretKey => Engine == DatabaseEngine.Sqlite
        ? $"PelnaKsiegowosc/db/Sqlite/{Path.GetFullPath(SqlitePath ?? "").ToLowerInvariant()}"
        : $"PelnaKsiegowosc/db/{Engine}/{Host}:{Port}/{Database}/{Username}";

    /// <param name="password">PostgreSQL: hasło. SQLite: nie dotyczy — klucz szyfrowania pliku przekazuje się do
    /// <see cref="DbContextFactory.Create"/> (ustawiany przy otwarciu połączenia).</param>
    /// <param name="encrypted">SQLite szyfrowane: klucze obce włącza się po ustawieniu klucza (pierwsza instrukcja musi ustawić klucz).</param>
    public string BuildConnectionString(string? password, bool encrypted = false)
    {
        switch (Engine)
        {
            case DatabaseEngine.Sqlite:
                if (string.IsNullOrWhiteSpace(SqlitePath)) throw new InvalidOperationException("Nie wskazano pliku bazy SQLite.");
                return new SqliteConnectionStringBuilder
                {
                    DataSource = SqlitePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = encrypted ? null : true,
                    Pooling = false,
                    DefaultTimeout = TimeoutSeconds
                }.ToString();
            case DatabaseEngine.PostgreSql:
                if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username))
                    throw new InvalidOperationException("Profil PostgreSQL wymaga hosta, nazwy bazy i użytkownika.");
                if (!PostgresLoginPolicy.IsValid(Username))
                    throw new InvalidOperationException("Login PostgreSQL musi mieć maksymalnie 63 znaki, zaczynać się od małej litery lub _ i zawierać tylko małe litery, cyfry i _. Nie używaj zastrzeżonego prefiksu pg_.");
                // Require i VerifyCA szyfrują połączenie, ale nie weryfikują pełnej nazwy
                // hosta. Przy zdalnym serwerze ksiąg dopuszczamy wyłącznie VerifyFull,
                // włącznie z weryfikacją certyfikatu i nazwy podanego serwera.
                if (!IsLocalHost(Host) && SslMode != PgSslMode.VerifyFull)
                    throw new InvalidOperationException("Zdalne połączenie PostgreSQL wymaga TLS VerifyFull z weryfikacją certyfikatu i nazwy serwera. Zainstaluj poprawny certyfikat CA; samo Require/VerifyCA nie wystarcza.");
                var b = new NpgsqlConnectionStringBuilder
                {
                    Host = Host,
                    Port = Port,
                    Database = Database,
                    Username = Username,
                    Password = password,
                    Timeout = TimeoutSeconds,
                    CommandTimeout = Math.Max(30, TimeoutSeconds * 4),
                    SslMode = SslMode switch
                    {
                        PgSslMode.Disable => Npgsql.SslMode.Disable,
                        PgSslMode.Prefer => Npgsql.SslMode.Prefer,
                        PgSslMode.Require => Npgsql.SslMode.Require,
                        PgSslMode.VerifyCA => Npgsql.SslMode.VerifyCA,
                        _ => Npgsql.SslMode.VerifyFull
                    },
                    ApplicationName = "PelnaKsiegowosc",
                    IncludeErrorDetail = false
                };
                if (!string.IsNullOrWhiteSpace(RootCertificatePath)) b.RootCertificate = RootCertificatePath;
                return b.ToString();
            default:
                throw new NotSupportedException(Engine.ToString());
        }
    }

    public static bool IsLocalHost(string host) =>
        host.Trim().ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.StartsWith('/');

    /// <summary>Opis połączenia bez hasła (do logów i UI).</summary>
    public string SafeDescription => Engine == DatabaseEngine.Sqlite
        ? $"SQLite: {SqlitePath}"
        : $"PostgreSQL: {Username}@{Host}:{Port}/{Database} (TLS: {SslMode})";
}

public static class DbContextFactory
{
    // IMaterializationInterceptor is a singleton service in EF Core. Reuse this
    // stateless instance so each context does not create a new service provider.
    private static readonly ProtectedFieldsMaterializationInterceptor ProtectedFieldsInterceptor = new();

    public const string SqliteMigrationsAssembly = "PelnaKsiegowosc.Migrations.Sqlite";
    public const string PostgresMigrationsAssembly = "PelnaKsiegowosc.Migrations.PostgreSql";

    public static DbContextOptions<KsiegiDbContext> BuildOptions(DatabaseEngine engine, string connectionString, string? sqliteKey = null)
    {
        var b = new DbContextOptionsBuilder<KsiegiDbContext>();
        switch (engine)
        {
            case DatabaseEngine.Sqlite:
                b.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly));
                if (sqliteKey is not null) b.AddInterceptors(new SqliteKeyInterceptor(sqliteKey));
                break;
            case DatabaseEngine.PostgreSql:
                b.UseNpgsql(connectionString, o =>
                {
                    o.MigrationsAssembly(PostgresMigrationsAssembly);
                    o.EnableRetryOnFailure(0);
                });
                b.AddInterceptors(ProtectedFieldsInterceptor);
                break;
        }
        b.EnableSensitiveDataLogging(false);
        return b.Options;
    }

    public static KsiegiDbContext Create(DatabaseEngine engine, string connectionString, string? sqliteKey = null) =>
        new(BuildOptions(engine, connectionString, sqliteKey));
}
