namespace PelnaKsiegowosc.Domain.Common;

/// <summary>
/// Bazowa encja. Identyfikatory GUID (wersja 7 — uporządkowane czasowo) są globalnie unikalne,
/// co pozwala bezpiecznie migrować dane między SQLite a PostgreSQL z zachowaniem identyfikatorów
/// oraz uzgadniać kopie robocze trybu awaryjnego.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>Encja przypisana do jednostki. Przygotowanie pod obsługę wielu jednostek.</summary>
public abstract class CompanyEntity : Entity
{
    public Guid CompanyId { get; set; }
}

/// <summary>Zegar systemowy — abstrakcja dla testowalności. Strefa czasowa księgowa: Europe/Warsaw.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly TimeZoneInfo Warsaw = FindWarsaw();

    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Warsaw));

    public static DateTime ToWarsaw(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Warsaw);

    private static TimeZoneInfo FindWarsaw()
    {
        foreach (var id in new[] { "Europe/Warsaw", "Central European Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }
}

public sealed class FixedClock : IClock
{
    public FixedClock(DateTime utcNow) { UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc); }
    public DateTime UtcNow { get; set; }
    public DateOnly Today => DateOnly.FromDateTime(SystemClock.ToWarsaw(UtcNow));
}

public static class DateFormat
{
    public const string Pl = "dd.MM.yyyy";
    public static string ToPl(this DateOnly d) => d.ToString(Pl, System.Globalization.CultureInfo.InvariantCulture);
    public static string ToPl(this DateOnly? d) => d?.ToPl() ?? "";
    public static string ToPlDateTime(this DateTime utc) =>
        SystemClock.ToWarsaw(utc).ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParsePl(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);
}
