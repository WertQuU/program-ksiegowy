using System.Security.Cryptography;
using System.Text;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Presentation.Services;

/// <summary>
/// Program nie ma własnego logowania — dostęp do danych chroni konto systemu operacyjnego (Windows/Linux),
/// a w trybie serwerowym dodatkowo konto PostgreSQL. Operatorem w dzienniku audytu i na dokumentach jest
/// zalogowany użytkownik systemu; jego identyfikator jest stały dla danej nazwy konta i komputera.
/// </summary>
public static class OperatorIdentity
{
    public static readonly Role[] Roles = { Role.Administrator, Role.GlownyKsiegowy, Role.Zarzad };

    public static string Login => $"{Environment.UserName}@{Environment.MachineName}";

    public static Guid IdFor(string login)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("PelnaKsiegowosc.Operator|" + login.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static CurrentUser For(Guid companyId) => new(IdFor(Login), Login, companyId, Roles);
}
