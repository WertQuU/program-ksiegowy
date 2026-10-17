using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Security;

namespace PsaKsiegi.Application.Abstractions;

/// <summary>Kontekst bieżącego użytkownika i jednostki.</summary>
public interface ICurrentUser
{
    Guid UserId { get; }
    string Login { get; }
    Guid CompanyId { get; }
    IReadOnlyList<Role> Roles { get; }
    bool Can(Permission permission);
}

public sealed class CurrentUser : ICurrentUser
{
    public CurrentUser(Guid userId, string login, Guid companyId, IEnumerable<Role> roles)
    {
        UserId = userId;
        Login = login;
        CompanyId = companyId;
        Roles = roles.ToList();
    }

    public Guid UserId { get; }
    public string Login { get; }
    public Guid CompanyId { get; set; }
    public IReadOnlyList<Role> Roles { get; }
    public bool Can(Permission permission) => RolePermissions.Has(Roles, permission);

    /// <summary>Kontekst systemowy (kreator pierwszego uruchomienia, migracje) — pełne uprawnienia, identyfikowany w audycie jako „system”.</summary>
    public static CurrentUser System(Guid companyId) => new(Guid.Empty, "system", companyId, new[] { Role.Administrator });
}

public static class PermissionGuard
{
    public static void Require(this ICurrentUser user, Permission p)
    {
        if (!user.Can(p)) throw new AccessDeniedException($"Brak uprawnienia: {p}. Skontaktuj się z administratorem.");
    }
}

/// <summary>Magazyn plików załączników (lokalny katalog lub inny). Zawartość weryfikowana sumą SHA-256.</summary>
public interface IFileStore
{
    Task<(string storagePath, string sha256, long size)> SaveAsync(Stream content, string suggestedName, CancellationToken ct = default);
    Task<Stream> OpenAsync(string storagePath, CancellationToken ct = default);
    Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default);
    string RootPath { get; }
}

/// <summary>
/// Magazyn sekretów (hasła baz danych, klucze Object Storage, tokeny KSeF). Implementacja produkcyjna:
/// Windows Credential Manager. Sekrety nigdy nie są zapisywane w plikach konfiguracji ani logach.
/// </summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string secret);
    void Delete(string key);
    string ProviderName { get; }
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _items = new();
    public string? Get(string key) => _items.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string secret) => _items[key] = secret;
    public void Delete(string key) => _items.Remove(key);
    public string ProviderName => "Pamięć (tylko testy)";
}
