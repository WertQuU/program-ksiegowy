using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PelnaKsiegowosc.Application.Abstractions;

namespace PelnaKsiegowosc.Infrastructure.Secrets;

/// <summary>
/// Magazyn sekretów oparty na Menedżerze poświadczeń Windows (Credential Manager, typ GENERIC, trwałość LOCAL_MACHINE —
/// sekrety są chronione kontem użytkownika Windows). Hasła, klucze S3 i tokeny KSeF nigdy nie trafiają do plików ani logów.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string ProviderName => "Menedżer poświadczeń Windows";

    public string? Get(string key)
    {
        if (!CredRead(key, CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return null;
            throw new Win32Exception(err, "Odczyt sekretu z Menedżera poświadczeń nie powiódł się.");
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }

    public void Set(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        if (blob.Length > 5 * 512) throw new ArgumentException("Sekret jest zbyt długi dla Menedżera poświadczeń.");
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = key,
                CredentialBlobSize = blob.Length,
                CredentialBlob = ptr,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "Pełna Księgowość"
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Zapis sekretu w Menedżerze poświadczeń nie powiódł się.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(key, CredTypeGeneric, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ErrorNotFound) throw new Win32Exception(err);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>
/// Magazyn plikowy: sekrety szyfrowane AES-GCM kluczem z osobnego pliku; oba pliki z uprawnieniami 0600.
/// Ochrona opiera się na uprawnieniach konta użytkownika systemu — stosowany na Linuksie, gdy usługa
/// Secret Service (pęk kluczy GNOME/KDE) jest niedostępna.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _file;
    private readonly byte[] _key;

    public FileSecretStore(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _file = Path.Combine(directory, "sekrety.bin");
        var keyFile = Path.Combine(directory, "sekrety.key");
        if (!File.Exists(keyFile))
        {
            File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _key = File.ReadAllBytes(keyFile);
    }

    public string ProviderName => "Plik zaszyfrowany w katalogu użytkownika (uprawnienia 0600)";

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_file)) return new();
        var p = new Application.Security.DataProtector(_key);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(p.Unprotect(File.ReadAllText(_file), "secrets"))!;
    }

    private void Save(Dictionary<string, string> d)
    {
        var p = new Application.Security.DataProtector(_key);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, p.Protect(JsonSerializer.SerializeToUtf8Bytes(d), "secrets"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _file, true);
    }

    public string? Get(string key) => Load().TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string secret) { var d = Load(); d[key] = secret; Save(d); }
    public void Delete(string key) { var d = Load(); if (d.Remove(key)) Save(d); }
}

/// <summary>
/// Linux: usługa Secret Service (libsecret — pęk kluczy GNOME, KWallet) przez narzędzie <c>secret-tool</c>.
/// Sekret przekazywany jest przez standardowe wejście, nigdy w argumentach wiersza poleceń.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string Service = "PelnaKsiegowosc";

    public string ProviderName => "Pęk kluczy systemu (Secret Service / libsecret)";

    public string? Get(string key)
    {
        var (code, output, _) = Run(null, "lookup", "service", Service, "key", key);
        return code == 0 ? output : null;
    }

    public void Set(string key, string secret)
    {
        var (code, _, err) = Run(secret, "store", "--label", $"Pełna Księgowość — {key}", "service", Service, "key", key);
        if (code != 0) throw new InvalidOperationException("Zapis sekretu w pęku kluczy nie powiódł się: " + err.Trim());
    }

    public void Delete(string key) => Run(null, "clear", "service", Service, "key", key);

    /// <summary>Sprawdza, czy <c>secret-tool</c> jest zainstalowany i pęk kluczy przyjmuje zapis (próba zapisu, odczytu i usunięcia).</summary>
    public static bool IsAvailable()
    {
        try
        {
            var store = new LinuxSecretServiceStore();
            var probe = "__test__" + Guid.NewGuid().ToString("N");
            var value = Guid.NewGuid().ToString("N");
            store.Set(probe, value);
            var ok = store.Get(probe) == value;
            store.Delete(probe);
            return ok;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static (int code, string output, string error) Run(string? stdin, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Nie można uruchomić secret-tool.");
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000))
        {
            p.Kill();
            throw new InvalidOperationException("Przekroczono czas oczekiwania na pęk kluczy.");
        }
        return (p.ExitCode, output, error);
    }
}

public static class SecretStoreFactory
{
    /// <summary>
    /// Windows: Menedżer poświadczeń. Linux: pęk kluczy (Secret Service), a gdy niedostępny — zaszyfrowany plik w katalogu danych.
    /// Zmienna PELNAKSIEGOWOSC_SECRETS=plik wymusza magazyn plikowy (np. serwer bez pęku kluczy, testy).
    /// </summary>
    public static ISecretStore Create(Configuration.AppPaths paths)
    {
        var file = Environment.GetEnvironmentVariable("PELNAKSIEGOWOSC_SECRETS") == "plik";
        if (!file && OperatingSystem.IsWindows()) return new WindowsCredentialSecretStore();
        if (!file && OperatingSystem.IsLinux() && LinuxSecretServiceStore.IsAvailable()) return new LinuxSecretServiceStore();
        return new FileSecretStore(Path.Combine(paths.Root, "Sekrety"));
    }
}
