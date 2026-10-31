using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Integrity;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Infrastructure.Migration;
using PelnaKsiegowosc.Infrastructure.Secrets;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Cli;

/// <summary>
/// Narzędzie wiersza poleceń: kopie zapasowe, weryfikacja i odtwarzanie archiwów, kontrola integralności, migracja na serwer.
/// Korzysta z konfiguracji i magazynu sekretów tego samego stanowiska co program okienkowy.
/// </summary>
public static class Program
{
    private const string Usage = """
        Pełna Księgowość — narzędzie wiersza poleceń

        Użycie: pk-cli <polecenie> [opcje]

          info                                  konfiguracja stanowiska, baza, jednostka, lata obrotowe
          kopia [--powod TEKST] [--bez-wysylki] szyfrowana kopia zapasowa aktywnej bazy (klucz z magazynu sekretów)
          weryfikuj PLIK                        weryfikacja archiwum kopii (sumy, szyfrowanie, kompletność)
          odtworz PLIK KATALOG                  odtworzenie kopii do nowego katalogu (baza SQLite + załączniki)
          integralnosc                          pełna kontrola integralności ksiąg (kod wyjścia 1 przy nieprawidłowościach)
          migruj --host H --baza B --uzytkownik U [--port 5432] [--ssl VerifyFull|Require|Prefer|Disable]
                 [--przelacz]                   migracja danych do pustej bazy PostgreSQL (hasło: zmienna PK_DB_HASLO)
          pomoc                                 ten opis

        Klucz kopii: zapisany na stanowisku albo podany w zmiennej PK_KLUCZ_ODZYSKIWANIA (klucz odzyskiwania)
        lub PK_HASLO_KOPII (hasło kopii). Katalog danych: zmienna PELNAKSIEGOWOSC_HOME (domyślnie katalog użytkownika).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "pomoc" or "-h" or "--help" or "/?")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var paths = new AppPaths();
            ErrorLog.Directory = paths.LogsDirectory;
            var session = new AppSession(paths, AppConfig.LoadOrCreate(paths), SecretStoreFactory.Create(paths), new SystemClock());
            return args[0] switch
            {
                "info" => await InfoAsync(session),
                "kopia" => await BackupAsync(session, args),
                "weryfikuj" => await VerifyAsync(session, Arg(args, 1, "PLIK")),
                "odtworz" => await RestoreAsync(session, Arg(args, 1, "PLIK"), Arg(args, 2, "KATALOG")),
                "integralnosc" => await IntegrityAsync(session),
                "migruj" => await MigrateAsync(session, args),
                _ => Fail($"Nieznane polecenie „{args[0]}”. Użyj: pk-cli pomoc")
            };
        }
        catch (DomainException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            ErrorLog.Write(ex);
            return Fail(Presentation.ViewModels.ViewModelBase.Describe(ex));
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("BŁĄD: " + message);
        return 1;
    }

    private static string Arg(string[] args, int index, string name) =>
        args.Length > index ? args[index] : throw new DomainException($"Brak argumentu {name}. Użyj: pk-cli pomoc");

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static async Task OpenAsync(AppSession session)
    {
        if (!session.Config.SetupCompleted) throw new DomainException("Stanowisko nie jest skonfigurowane — uruchom najpierw program i kreator pierwszego uruchomienia.");
        var r = await session.OpenAsync();
        if (!r.Success) throw new DomainException(r.Error ?? "Nie udało się otworzyć bazy.");
        if (r.PreMigrationBackup is { } b) Console.WriteLine($"Zaktualizowano schemat bazy; kopia przed aktualizacją: {b}");
    }

    private static BackupKey Key(AppSession session)
    {
        if (Environment.GetEnvironmentVariable("PK_KLUCZ_ODZYSKIWANIA") is { Length: > 0 } rk)
            return new BackupKey.RawKey(Presentation.ViewModels.Sections.BackupsViewModel.TryParseRecoveryKey(rk) ?? throw new DomainException("Nieprawidłowy klucz w PK_KLUCZ_ODZYSKIWANIA."));
        if (Environment.GetEnvironmentVariable("PK_HASLO_KOPII") is { Length: > 0 } pass) return new BackupKey.Passphrase(pass);
        return session.StoredBackupKey() ?? throw new DomainException("Brak klucza kopii — ustaw PK_KLUCZ_ODZYSKIWANIA albo zapisz klucz w programie (sekcja „Backupy”).");
    }

    private static async Task<int> InfoAsync(AppSession session)
    {
        Console.WriteLine($"Katalog danych:   {session.Paths.Root}");
        Console.WriteLine($"Magazyn sekretów: {session.Secrets.ProviderName}");
        Console.WriteLine($"Baza:             {session.DatabaseDescription}");
        Console.WriteLine($"Kopie:            {session.BackupDirectory} (klucz {(session.StoredBackupKey() is null ? "NIEZAPISANY" : "zapisany")})");
        Console.WriteLine($"Object Storage:   {(session.Config.ObjectStorage.Enabled ? $"{session.Config.ObjectStorage.Endpoint}/{session.Config.ObjectStorage.Bucket}" : "wyłączony")}");
        if (!session.Config.SetupCompleted) { Console.WriteLine("Stanowisko nieskonfigurowane."); return 0; }
        await OpenAsync(session);
        var c = session.Company!;
        Console.WriteLine($"Jednostka:        {c.FullName} ({LegalForms.Name(c.LegalForm)}), NIP {c.Nip}");
        foreach (var y in session.Years) Console.WriteLine($"Rok obrotowy:     {y.Name} {y.StartDate.ToPl()}–{y.EndDate.ToPl()} ({y.Status})");
        return 0;
    }

    private static async Task<int> BackupAsync(AppSession session, string[] args)
    {
        await OpenAsync(session);
        var r = await session.BackupAsync(Option(args, "--powod") ?? "wiersz poleceń", upload: !args.Contains("--bez-wysylki"));
        Console.WriteLine($"Kopia: {r.FilePath}");
        Console.WriteLine($"Rozmiar: {r.SizeBytes:N0} B; SHA-256: {r.Sha256}; zweryfikowana: {(r.Verified ? "tak" : "NIE")}");
        if (r.RemoteKey is not null) Console.WriteLine($"Object Storage: {r.RemoteKey}");
        if (r.RemoteError is not null) Console.Error.WriteLine($"Błąd wysyłki do Object Storage: {r.RemoteError}");
        return r.Verified && r.RemoteError is null ? 0 : 1;
    }

    private static async Task<int> VerifyAsync(AppSession session, string file)
    {
        var r = await new BackupService(session.Clock, session.Paths.TempDirectory).VerifyAsync(file, Key(session));
        if (r.Ok)
        {
            Console.WriteLine($"Kopia poprawna: {r.Manifest!.CompanyName} (NIP {r.Manifest.CompanyNip}), utworzona {r.Manifest.CreatedUtc.ToPlDateTime()}, program {r.Manifest.AppVersion}, plików {r.Manifest.Files.Count}.");
            return 0;
        }
        foreach (var p in r.Problems) Console.Error.WriteLine("  " + p);
        return Fail("Kopia nie przeszła weryfikacji.");
    }

    private static (string Db, byte[] Attachments) RestoreKeys(AppSession session, string directory)
    {
        var profile = new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = Path.Combine(directory, "ksiegi.db") };
        var db = session.Connector.SqliteKeyForNewFile(profile);
        return (db, session.AttachmentsKeyFor(Path.Combine(directory, "Zalaczniki"), profile));
    }

    private static async Task<int> RestoreAsync(AppSession session, string file, string directory)
    {
        var r = await new BackupService(session.Clock, session.Paths.TempDirectory).RestoreToSqliteAsync(file, Key(session), Path.GetFullPath(directory),
            RestoreKeys(session, Path.GetFullPath(directory)).Db, RestoreKeys(session, Path.GetFullPath(directory)).Attachments);
        if (!r.Ok)
        {
            foreach (var p in r.Problems) Console.Error.WriteLine("  " + p);
            return Fail("Odtwarzanie nie powiodło się.");
        }
        Console.WriteLine($"Odtworzono bazę: {r.DatabasePath}");
        Console.WriteLine($"Załączniki:      {r.AttachmentsPath}");
        Console.WriteLine("Aby pracować na odtworzonej bazie, wskaż ją w programie: Ustawienia → Baza danych.");
        return 0;
    }

    private static async Task<int> IntegrityAsync(AppSession session)
    {
        await OpenAsync(session);
        await using var db = session.OpenDb();
        var r = await new IntegrityService(db, session.Clock).FullCheckAsync(session.CompanyId);
        foreach (var (area, ok, details) in r.Results) Console.WriteLine($"{(ok ? "OK   " : "BŁĄD ")} {area}: {details}");
        return r.AllOk ? 0 : 1;
    }

    private static async Task<int> MigrateAsync(AppSession session, string[] args)
    {
        await OpenAsync(session);
        var target = new DatabaseProfile
        {
            Name = "Serwer PostgreSQL", Engine = DatabaseEngine.PostgreSql,
            Host = Option(args, "--host") ?? throw new DomainException("Podaj --host."),
            Port = int.TryParse(Option(args, "--port"), out var port) ? port : 5432,
            Database = Option(args, "--baza") ?? throw new DomainException("Podaj --baza."),
            Username = Option(args, "--uzytkownik") ?? throw new DomainException("Podaj --uzytkownik."),
            SslMode = Enum.TryParse<PgSslMode>(Option(args, "--ssl"), true, out var ssl) ? ssl : PgSslMode.VerifyFull
        };
        if (Environment.GetEnvironmentVariable("PK_DB_HASLO") is { Length: > 0 } pwd) session.Connector.SavePassword(target, pwd);
        var test = await session.Connector.TestAsync(target);
        if (!test.Success) return Fail(test.Message);
        if (!test.DatabaseEmpty) return Fail("Baza docelowa zawiera dane — migracja wymaga pustej bazy.");
        await using var source = session.OpenDb();
        await using var dest = session.Connector.CreateContext(target);
        var report = await new DatabaseMigrator(session.Clock, session.Paths.TempDirectory).MigrateAsync(source, session.DatabaseDescription, session.Attachments,
            dest, target.SafeDescription, session.Attachments, async () => (await session.BackupAsync("przed migracją bazy (CLI)", upload: true)).FilePath);
        Console.WriteLine(report.ToText());
        await session.AuditAsync(report.Success ? "MIGRACJA_ZAKONCZONA" : "MIGRACJA_NIEUDANA", "Database", null, $"{session.DatabaseDescription} → {target.SafeDescription} (CLI)");
        if (report.Success && args.Contains("--przelacz"))
        {
            session.Config.OtherProfiles.Add(session.Config.ActiveDatabase);
            session.Config.ActiveDatabase = target;
            session.SaveConfig();
            Console.WriteLine("Stanowisko przełączono na bazę serwerową.");
        }
        return report.Success ? 0 : 1;
    }
}
