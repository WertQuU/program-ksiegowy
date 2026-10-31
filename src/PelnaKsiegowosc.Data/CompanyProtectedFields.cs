using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Payroll;

namespace PelnaKsiegowosc.Data;

/// <summary>
/// Wyłącznie pola prywatne, niewymagane do filtrowania/agregacji w SQL.
/// Kwoty, numery kont, historia księgowa, daty i NIP pozostają w dotychczasowym formacie.
/// Brak zmian schematu tabel: tekst jest zapisywany jako PK1$ + base64.
/// </summary>
public static class CompanyProtectedFields
{
    public static IReadOnlyList<string> For(Type type)
    {
        if (type == typeof(Employee)) return ["Pesel", "BankAccountIban", "Address"];
        if (type == typeof(ResponsiblePerson)) return ["Pesel"];
        if (type == typeof(Counterparty)) return ["Pesel"];
        return Array.Empty<string>();
    }

    public static void DecodeMaterialized(object entity)
    {
        if (entity is not CompanyEntity data) return;
        foreach (var field in For(entity.GetType()))
        {
            var prop = entity.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || prop.GetValue(entity) is not string stored || !CompanyFieldCodec.IsProtected(stored)) continue;
            prop.SetValue(entity, CompanyFieldCodec.Unprotect(stored, data.CompanyId, data.Id, field));
        }
    }
}

/// <summary>
/// Po zapytaniu PostgreSQL obiekt trafia do UI i usług już w postaci jawnej,
/// a w tabelach pozostaje zaszyfrowany tekst.
/// </summary>
public sealed class ProtectedFieldsMaterializationInterceptor : IMaterializationInterceptor
{
    public InterceptionResult<object> CreatingInstance(MaterializationInterceptionData data, InterceptionResult<object> result) => result;
    public object CreatedInstance(MaterializationInterceptionData data, object entity) => entity;
    public InterceptionResult InitializingInstance(MaterializationInterceptionData data, object entity, InterceptionResult result) => result;
    public object InitializedInstance(MaterializationInterceptionData data, object entity)
    {
        if (data.Context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            CompanyProtectedFields.DecodeMaterialized(entity);
        return entity;
    }
}
