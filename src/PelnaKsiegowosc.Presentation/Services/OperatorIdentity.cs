using System.Security.Cryptography;
using System.Text;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>
/// Program nie ma własnego logowania — dostęp do danych chroni konto systemu operacyjnego (Windows/Linux),
/// a w trybie serwerowym dodatkowo konto PostgreSQL. Operatorem w dzienniku audytu i na dokumentach jest
/// zalogowany użytkownik systemu; jego identyfikator jest stały dla danej nazwy konta i komputera.
/// Role operatora pochodzą z bazy (OperatorAccessService) — role założyciela przysługują wyłącznie przy zakładaniu ksiąg.
/// </summary>
public static class OperatorIdentity
{
    public static IReadOnlyList<Role> FounderRoles => Application.Security.OperatorAccessService.FounderRoles;

    public static string Login => $"{Environment.UserName}@{Environment.MachineName}";

    public static Guid IdFor(string login) => Application.Security.OperatorAccessService.IdFor(login);

    /// <summary>Tożsamość przy zakładaniu ksiąg (kreator) — operator zakładający księgi jest ich pierwszym administratorem.</summary>
    public static CurrentUser For(Guid companyId) => new(IdFor(Login), Login, companyId, FounderRoles);

    public static CurrentUser For(Guid companyId, IEnumerable<Role> roles) => new(IdFor(Login), Login, companyId, roles);
}
