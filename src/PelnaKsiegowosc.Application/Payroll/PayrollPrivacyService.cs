using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Payroll;

/// <summary>Removes medical cause categories from legacy payroll settings; preserves rate and evidence reference.</summary>
public sealed class PayrollPrivacyService(KsiegiDbContext db, ICurrentUser user, IClock clock)
{
    public static SickWageAssessment ReadAssessment(JsonObject source)
    {
        var value = source.DeepClone().AsObject();
        if (value["RatePercent"] is null && value["Cause"] is JsonValue cause && cause.TryGetValue<int>(out var code))
            value["RatePercent"] = code switch { 0 => 80m, 1 or 2 or 3 or 4 => 100m,
                _ => throw new DomainException("Nieprawidłowe potwierdzenie stawki chorobowego. Potwierdź je ponownie.") };
        value.Remove("Cause");
        var result = value.Deserialize<SickWageAssessment>()
            ?? throw new DomainException("Nieprawidłowe potwierdzenie stawki chorobowego.");
        if (result.RatePercent is not (80m or 100m)) throw new DomainException("Potwierdź ponownie stawkę 80% lub 100%.");
        return result;
    }

    public async Task<int> MinimizeLegacySettingsAsync(CancellationToken ct = default)
    {
        user.Require(Permission.ManagePayrollData);
        var settings = await db.Settings.Where(s => s.CompanyId == user.CompanyId &&
            s.Key.StartsWith("place.chorobowe.weryfikacja.")).ToListAsync(ct);
        var count = 0;
        foreach (var setting in settings)
        {
            var data = JsonNode.Parse(setting.Value)?.AsObject();
            if (data is null || !data.ContainsKey("Cause")) continue;
            setting.Value = JsonSerializer.Serialize(ReadAssessment(data));
            setting.UpdatedAtUtc = clock.UtcNow;
            count++;
        }
        if (count != 0)
        {
            await new AuditService(db, clock).AddAsync(user, "DANE_KADROWE_ZMINIMALIZOWANE", "Payroll", null,
                $"Usunięto kategorie przyczyn; zachowano wyłącznie stawki i odnośniki do dokumentacji: {count}.", ct);
            await db.SaveChangesAsync(ct);
        }
        return count;
    }
}
