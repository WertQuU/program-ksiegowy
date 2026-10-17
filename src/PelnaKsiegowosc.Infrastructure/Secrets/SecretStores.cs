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
                Comment = "Pełna Księgowość P.S.A."
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
/// Magazyn plikowy wyłącznie na potrzeby rozwoju i testów na systemach innych niż Windows: sekrety szyfrowane AES-GCM
/// kluczem z pliku o uprawnieniach 0600. NIE jest używany w wersji produkcyjnej dla Windows.
/// </summary>
public sealed class DevelopmentFileSecretStore : ISecretStore
{
    private readonly string _file;
    private readonly byte[] _key;

    public DevelopmentFileSecretStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "sekrety.dev.bin");
        var keyFile = Path.Combine(directory, "sekrety.dev.key");
        if (!File.Exists(keyFile))
        {
            File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _key = File.ReadAllBytes(keyFile);
    }

    public string ProviderName => "Plik deweloperski (NIE do produkcji)";

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_file)) return new();
        var p = new Application.Security.DataProtector(_key);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(p.Unprotect(File.ReadAllText(_file), "secrets"))!;
    }

    private void Save(Dictionary<string, string> d)
    {
        var p = new Application.Security.DataProtector(_key);
        File.WriteAllText(_file, p.Protect(JsonSerializer.SerializeToUtf8Bytes(d), "secrets"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public string? Get(string key) => Load().TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string secret) { var d = Load(); d[key] = secret; Save(d); }
    public void Delete(string key) { var d = Load(); if (d.Remove(key)) Save(d); }
}

public static class SecretStoreFactory
{
    public static ISecretStore Create(Configuration.AppPaths paths) =>
        OperatingSystem.IsWindows() ? new WindowsCredentialSecretStore() : new DevelopmentFileSecretStore(Path.Combine(paths.Root, "dev-sekrety"));
}
