using System.Reflection;

namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>Warunki licencji akceptowane przy pierwszym uruchomieniu (treść pliku LICENSE dołączona do programu).</summary>
public static class Terms
{
    public const string Version = "1.1";
    public const string SettingKey = "terms.accepted";

    public static string Text
    {
        get
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PelnaKsiegowosc.LICENSE")
                ?? throw new InvalidOperationException("Brak treści licencji w programie.");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }

    public sealed record Acceptance(string Version, string Login, DateTime AtUtc);
}
