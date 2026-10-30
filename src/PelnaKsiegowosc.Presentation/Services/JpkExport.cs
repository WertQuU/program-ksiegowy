using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Vat;
using PelnaKsiegowosc.Domain.Vat;
using PelnaKsiegowosc.Infrastructure.Jpk;

namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>Generowanie pliku JPK_V7M / JPK_V7K z zapisaniem informacji o wygenerowaniu (dla kalendarza terminów).</summary>
public static class JpkExport
{
    public static async Task<bool> GenerateAsync(AppSession session, IDialogService dialogs, DateOnly period, decimal? carriedForward, bool correction)
    {
        await using var db = session.OpenDb();
        var vat = new VatService(db, session.User, session.Clock);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == session.CompanyId);
        var sales = await vat.RegisterAsync(VatRegisterType.Sprzedaz, period);
        var purchases = await vat.RegisterAsync(VatRegisterType.Zakup, period);
        var quarterly = company.VatPeriod == Domain.Companies.VatSettlementPeriod.Kwartalny;
        var kind = quarterly ? "JPK_V7K" : "JPK_V7M";
        var monthControl = await vat.ComputeDeclarationAsync(period, carriedForward, monthOnly: true);
        var decl = await vat.IsDeclarationMonthAsync(period) ? await vat.ComputeDeclarationAsync(period, carriedForward) : null;
        var r = JpkV7Builder.Build(company, period, sales, purchases, decl, monthControl, correction, session.Clock.UtcNow, session.Validator);
        var target = await dialogs.SaveFileAsync($"Zapisz {kind}", $"{kind}-{period:yyyy-MM}{(correction ? "-korekta" : "")}.xml", new FileFilter("XML", "*.xml"));
        if (target is null) return false;
        r.Document.Save(target);
        var settings = new SettingsService(db, session.Clock);
        await settings.SetAsync<DateTime?>(session.CompanyId, $"jpk.wygenerowano.{period:yyyy-MM}", session.Clock.UtcNow);
        await db.SaveChangesAsync();
        await session.AuditAsync("JPK_WYGENEROWANY", "JPK", null, $"{period:yyyy-MM}; {(correction ? "korekta" : "złożenie")}; {r.Status}");
        await dialogs.ShowTextAsync(kind, $"Zapisano: {target}{Environment.NewLine}Status: {r.Status}{Environment.NewLine}" +
            (decl is null ? "Plik zawiera wyłącznie ewidencję (deklaracja kwartalna w ostatnim miesiącu kwartału)." : $"{(decl.Quarterly ? $"Deklaracja za {decl.Quarter} kwartał. " : "")}Do zapłaty: {decl.Payable:N0} zł; nadwyżka do przeniesienia: {decl.Surplus:N0} zł") +
            $"{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, r.Warnings.Concat(r.Validation.Errors.Take(30))) +
            $"{Environment.NewLine}{Environment.NewLine}Plik wyślij przez e-Deklaracje / e-mikrofirmę lub inne narzędzie MF (wymaga podpisu). Program nie wysyła JPK.");
        return true;
    }
}
