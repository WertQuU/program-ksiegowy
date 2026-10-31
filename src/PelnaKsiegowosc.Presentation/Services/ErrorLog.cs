namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>
/// Minimalny lokalny rejestr błędów. Nie zapisuje treści wyjątków, parametrów SQL, stack trace ani ścieżek:
/// zawierają one potencjalnie PESEL, dane kontrahentów, hasła i tokeny. Diagnostyka zawiera jedynie typ wyjątku.
/// </summary>
public static class ErrorLog
{
    public static string? Directory { get; set; }

    public static void Write(Exception ex)
    {
        if (Directory is null) return;
        try
        {
            var root = Path.GetFullPath(Directory);
            System.IO.Directory.CreateDirectory(root);
            if (!OperatingSystem.IsWindows())
                System.IO.Directory.SetUnixFileMode(root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(root, $"bledy-{DateTime.UtcNow:yyyy-MM}.log");
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine($"[{DateTime.UtcNow:O}] {ex.GetType().FullName}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
