namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>Lokalny log błędów technicznych (bez danych księgowych i sekretów — tylko typ, komunikat i stos wywołań).</summary>
public static class ErrorLog
{
    public static string? Directory { get; set; }

    public static void Write(Exception ex)
    {
        if (Directory is null) return;
        try
        {
            var file = Path.Combine(Directory, $"bledy-{DateTime.UtcNow:yyyy-MM}.log");
            File.AppendAllText(file, $"[{DateTime.UtcNow:O}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
