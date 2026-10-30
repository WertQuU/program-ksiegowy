using System.Text.Json;
using System.Text.Json.Serialization;
using PelnaKsiegowosc.Data;

namespace PelnaKsiegowosc.Infrastructure.Configuration;

/// <summary>
/// Ścieżki danych użytkownika. Dane NIGDY nie są zapisywane w katalogu instalacyjnym programu.
/// Domyślnie: %LOCALAPPDATA%\PelnaKsiegowosc (Windows) lub ~/.local/share/PelnaKsiegowosc (Linux).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("PELNAKSIEGOWOSC_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PelnaKsiegowosc");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "konfiguracja.json");
    public string DataDirectory => Ensure(Path.Combine(Root, "Dane"));
    public string DefaultDatabaseFile => Path.Combine(DataDirectory, "ksiegi.db");
    public string AttachmentsDirectory => Ensure(Path.Combine(DataDirectory, "Zalaczniki"));
    public string DefaultBackupDirectory => Ensure(Path.Combine(Root, "Kopie"));
    public string LogsDirectory => Ensure(Path.Combine(Root, "Logi"));
    public string SchemasDirectory => Ensure(Path.Combine(Root, "Schematy"));
    public string ExportsDirectory => Ensure(Path.Combine(Root, "Eksport"));
    public string TempDirectory => Ensure(Path.Combine(Root, "Tmp"));

    private static string Ensure(string p)
    {
        Directory.CreateDirectory(p);
        return p;
    }
}

public sealed class BackupSettings
{
    public string? LocalDirectory { get; set; }
    public bool AutomaticEnabled { get; set; } = true;
    /// <summary>Odstęp kopii automatycznych w godzinach.</summary>
    public int IntervalHours { get; set; } = 24;
    public bool BackupOnExit { get; set; } = true;
    public int RetainLocalCount { get; set; } = 30;
    public bool UploadToObjectStorage { get; set; }
    public bool IncludeAttachments { get; set; } = true;
    /// <summary>Klucz kopii automatycznych przechowywany w magazynie sekretów (nazwa sekretu).</summary>
    public string KeySecretName { get; set; } = "PelnaKsiegowosc/backup-key";
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class ObjectStorageSettings
{
    public bool Enabled { get; set; }
    /// <summary>Np. https://s3.gra.io.cloud.ovh.net — endpoint właściwy dla regionu (weryfikuj w dokumentacji OVHcloud).</summary>
    public string? Endpoint { get; set; }
    public string? Region { get; set; }
    public string? Bucket { get; set; }
    public string Prefix { get; set; } = "pelna-ksiegowosc/";
    public bool ForcePathStyle { get; set; } = true;
    /// <summary>Szyfrowanie po stronie serwera (SSE-S3) — dodatkowo do szyfrowania archiwum po stronie klienta.</summary>
    public bool ServerSideEncryption { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int RetainRemoteCount { get; set; } = 90;
    public string AccessKeySecretName { get; set; } = "PelnaKsiegowosc/s3/access-key";
    public string SecretKeySecretName { get; set; } = "PelnaKsiegowosc/s3/secret-key";
    public DateTime? LastSuccessUtc { get; set; }
}

public enum KsefEnvironment
{
    Test,
    Demo,
    Produkcja
}

public sealed class KsefSettings
{
    public bool Enabled { get; set; }
    public KsefEnvironment Environment { get; set; } = KsefEnvironment.Test;
    public string TokenSecretName => $"PelnaKsiegowosc/ksef/{Environment}/token";
    public DateTime? LastSuccessUtc { get; set; }
}

/// <summary>Konfiguracja lokalna stanowiska (bez sekretów).</summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public int Version { get; set; } = 1;
    public DatabaseProfile ActiveDatabase { get; set; } = new();
    /// <summary>Skonfigurowane, ale nieaktywne profile (np. serwer jako cel kopii).</summary>
    public List<DatabaseProfile> OtherProfiles { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public ObjectStorageSettings ObjectStorage { get; set; } = new();
    public KsefSettings Ksef { get; set; } = new();
    public string? AttachmentsDirectory { get; set; }
    public bool SetupCompleted { get; set; }
    /// <summary>Motyw interfejsu: „Jasny”, „Ciemny” albo „Systemowy”.</summary>
    public string Theme { get; set; } = "Jasny";
    /// <summary>Wersja zaakceptowanych warunków licencji i chwila akceptacji (zapisywane też w bazie i dzienniku audytu).</summary>
    public string? TermsAcceptedVersion { get; set; }
    public DateTime? TermsAcceptedUtc { get; set; }

    public static AppConfig LoadOrCreate(AppPaths paths)
    {
        if (File.Exists(paths.ConfigFile))
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(paths.ConfigFile), Json) ?? new AppConfig();
        var c = new AppConfig { ActiveDatabase = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = paths.DefaultDatabaseFile } };
        return c;
    }

    public void Save(AppPaths paths)
    {
        var tmp = paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, paths.ConfigFile, true);
    }

    public string ToJsonWithoutSecrets() => JsonSerializer.Serialize(this, Json);
}
