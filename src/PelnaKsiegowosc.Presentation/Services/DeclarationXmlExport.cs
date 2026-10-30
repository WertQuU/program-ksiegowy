using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Infrastructure.Declarations;

namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>Zapis pliku XML deklaracji (e-Deklaracje) z walidacją oficjalnym schematem. Wysyłka i podpis — poza programem.</summary>
public static class DeclarationXmlExport
{
    /// <summary>Właściciel jednostki będącej osobą fizyczną (z osób odpowiedzialnych z numerem PESEL).</summary>
    public static async Task<TaxpayerPerson?> OwnerAsync(KsiegiDbContext db, Company company)
    {
        if (company.LegalForm != LegalForm.DzialalnoscGospodarczaOsobyFizycznej) return null;
        var persons = await db.ResponsiblePersons.AsNoTracking().Where(p => p.CompanyId == company.Id && p.Pesel != null).ToListAsync();
        var p = persons.FirstOrDefault(x => Domain.Validation.Identifiers.BirthDateFromPesel(x.Pesel) is not null);
        return p is null ? null : new TaxpayerPerson(p.FirstName, p.LastName, Domain.Validation.Identifiers.BirthDateFromPesel(p.Pesel)!.Value);
    }

    public static async Task<bool> SaveAsync(AppSession session, IDialogService dialogs, string kind, string fileName, DeclarationXmlResult r, string sendHint)
    {
        var target = await dialogs.SaveFileAsync($"Zapisz {kind} (XML)", fileName, new FileFilter("XML", "*.xml"));
        if (target is null) return false;
        r.Document.Save(target, SaveOptions.None);
        await session.AuditAsync("DEKLARACJA_XML", "Declaration", null, $"{kind}; {r.Status}");
        await dialogs.ShowTextAsync(kind, $"Zapisano: {target}{Environment.NewLine}Status: {r.Status}{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, r.Warnings.Concat(r.Validation.Errors.Take(30))) +
            $"{Environment.NewLine}{Environment.NewLine}{sendHint}");
        return true;
    }
}
