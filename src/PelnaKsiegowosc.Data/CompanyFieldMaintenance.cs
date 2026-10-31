using System.Data;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Data;

/// <summary>
/// Jednorazowa konwersja starszych jawnych wartości pól na deterministyczny
/// format PK1$ w istniejących tabelach. Wykonuj wyłącznie po kopii i po
/// uzyskaniu wyłącznej blokady konserwacyjnej serwera (poza tą klasą).
/// Nie tworzy nowych kolumn ani nie zmienia schematu.
/// </summary>
public static class CompanyFieldMaintenance
{
    public sealed record Result(int RecordsInspected, int ProtectedFieldsWritten);

    public static async Task<Result> ProtectExistingAsync(KsiegiDbContext db, Guid companyId,
        CancellationToken ct = default)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("Ta operacja służy wyłącznie aktualnej bazie PostgreSQL.");
        if (companyId == Guid.Empty)
            throw new InvalidOperationException("Nie wybrano firmy.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var employees = await db.Employees.Where(x => x.CompanyId == companyId).ToListAsync(ct);
        var people = await db.ResponsiblePersons.Where(x => x.CompanyId == companyId).ToListAsync(ct);
        var counterparties = await db.Counterparties.Where(x => x.CompanyId == companyId).ToListAsync(ct);
        var all = employees.Cast<CompanyEntity>().Concat(people).Concat(counterparties).ToList();
        var fields = 0;
        foreach (var entity in all)
        {
            foreach (var field in CompanyProtectedFields.For(entity.GetType()))
            {
                var property = db.Entry(entity).Property(field);
                if (property.CurrentValue is not string { Length: > 0 }) continue;
                // MaterializationInterceptor ujawnia dane programu, ale przed
                // SQL UPDATE zapis automatycznie zamienia je na ciphertext.
                property.IsModified = true;
                fields++;
            }
        }
        if (fields > 0) await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new Result(all.Count, fields);
    }
}
