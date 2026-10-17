using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PsaKsiegi.Data;

public enum DatabaseEngine
{
    Sqlite,
    PostgreSql
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

    public string PasswordSecretKey => $"PsaKsiegi/db/{Engine}/{Host}:{Port}/{Database}/{Username}";

    public string BuildConnectionString(string? password)
    {
        switch (Engine)
        {
            case DatabaseEngine.Sqlite:
                if (string.IsNullOrWhiteSpace(SqlitePath)) throw new InvalidOperationException("Nie wskazano pliku bazy SQLite.");
                return new SqliteConnectionStringBuilder
                {
                    DataSource = SqlitePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = true,
                    Pooling = false,
                    DefaultTimeout = TimeoutSeconds
                }.ToString();
            case DatabaseEngine.PostgreSql:
                if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username))
                    throw new InvalidOperationException("Profil PostgreSQL wymaga hosta, nazwy bazy i użytkownika.");
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
                    ApplicationName = "PsaKsiegi",
                    IncludeErrorDetail = false
                };
                if (!string.IsNullOrWhiteSpace(RootCertificatePath)) b.RootCertificate = RootCertificatePath;
                return b.ToString();
            default:
                throw new NotSupportedException(Engine.ToString());
        }
    }

    /// <summary>Opis połączenia bez hasła (do logów i UI).</summary>
    public string SafeDescription => Engine == DatabaseEngine.Sqlite
        ? $"SQLite: {SqlitePath}"
        : $"PostgreSQL: {Username}@{Host}:{Port}/{Database} (TLS: {SslMode})";
}

public static class DbContextFactory
{
    public const string SqliteMigrationsAssembly = "PsaKsiegi.Migrations.Sqlite";
    public const string PostgresMigrationsAssembly = "PsaKsiegi.Migrations.PostgreSql";

    public static DbContextOptions<KsiegiDbContext> BuildOptions(DatabaseEngine engine, string connectionString)
    {
        var b = new DbContextOptionsBuilder<KsiegiDbContext>();
        switch (engine)
        {
            case DatabaseEngine.Sqlite:
                b.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly));
                break;
            case DatabaseEngine.PostgreSql:
                b.UseNpgsql(connectionString, o =>
                {
                    o.MigrationsAssembly(PostgresMigrationsAssembly);
                    o.EnableRetryOnFailure(0);
                });
                break;
        }
        b.EnableSensitiveDataLogging(false);
        return b.Options;
    }

    public static KsiegiDbContext Create(DatabaseEngine engine, string connectionString) =>
        new(BuildOptions(engine, connectionString));
}
