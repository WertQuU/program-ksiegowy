using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Security;

public sealed record OperatorInfo(Guid Id, string Login, IReadOnlyList<Role> Roles, bool IsActive, DateTime? LastSeenUtc, DateTime CreatedAtUtc);

/// <summary>
/// Uprawnienia operatorów stanowisk. Program identyfikuje operatora za pomocą konta OS (login@komputer)
/// i oddzielnie uwierzytelnia połączenie w PostgreSQL. W bazie zapisane są role aplikacyjne.
/// Administrator powstaje TYLKO podczas zakładania nowych ksiąg; samo pierwsze połączenie
/// do istniejącej bazy NIE przyznaje praw. Pozostałych operatorów dodaje administrator.
/// WAŻNE: identyfikator OS nie jest kryptograficznym uwierzytelnieniem użytkownika w serwerze;
/// osoby mające bezpośrednie uprawnienia DML do bazy mogą ominąć zabezpieczenia aplikacji.
/// Produkcyjna izolacja wymaga osobnych kont PostgreSQL, ścisłych uprawnień albo warstwy API.
/// </summary>
public sealed class OperatorAccessService
{
    /// <summary>Oznaczenie kont operatorów systemowych w tabeli użytkowników (bez hasła).</summary>
    public const string SystemAccount = "SYSTEM";
    public static readonly Role[] FounderRoles = { Role.Administrator, Role.GlownyKsiegowy, Role.Zarzad };

    /// <summary>Identyfikator operatora/stanowiska zgodny z wcześniejszymi instalacjami.</summary>
    public static Guid IdFor(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) throw new DomainException("Podaj użytkownik@komputer.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("PelnaKsiegowosc.Operator|" + login.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private readonly KsiegiDbContext _db;
    private readonly IClock _clock;

    public OperatorAccessService(KsiegiDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Role operatora (zarejestrowanie przy pierwszym wejściu). Operator nieaktywny albo bez nadanych ról nie ma uprawnień.</summary>
    public async Task<IReadOnlyList<Role>> ResolveAsync(Guid operatorId, string login, Guid companyId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == operatorId, ct);
        if (user is null)
        {
            user = new User
            {
                Id = operatorId, Login = login, DisplayName = login, PasswordAlgorithm = SystemAccount, IsActive = true, CreatedAtUtc = _clock.UtcNow
            };
            // Nigdy nie nadawaj automatycznie admina pierwszemu klientowi istniejącej bazy.
            // Pierwszy administrator jest zapisywany w momencie tworzenia nowych ksiąg.
            user.SetRoles(Array.Empty<Role>());
            _db.Users.Add(user);
            var actor = new CurrentUser(operatorId, login, companyId, user.Roles);
            await new AuditService(_db, _clock).AddAsync(actor, "OPERATOR_NOWY_BEZ_UPRAWNIEN",
                "User", operatorId.ToString(), $"{login}: oczekuje na nadanie ról przez administratora", ct);
        }
        user.LastLoginAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return user.IsActive ? user.Roles : Array.Empty<Role>();
    }

    public async Task<List<OperatorInfo>> ListAsync(ICurrentUser actor, CancellationToken ct = default)
    {
        actor.Require(Permission.ManageUsers);
        return (await _db.Users.AsNoTracking().Where(u => u.PasswordAlgorithm == SystemAccount).OrderBy(u => u.Login).ToListAsync(ct))
            .Select(u => new OperatorInfo(u.Id, u.Login, u.Roles, u.IsActive, u.LastLoginAtUtc, u.CreatedAtUtc)).ToList();
    }

    /// <summary>Dodaje operatora stanowiska przed pierwszym połączeniem. Nie tworzy konta DB.</summary>
    public async Task<OperatorInfo> RegisterAsync(ICurrentUser admin, string login, IEnumerable<Role> roles, CancellationToken ct = default)
    {
        admin.Require(Permission.ManageUsers);
        login = (login ?? "").Trim();
        // Połączenie do współdzielonego PostgreSQL: każdy operator ma osobny login DB
        // reprezentowany przez db:<login>. Dla lokalnego SQLite zachowujemy OS user@host.
        var serverUser = login.StartsWith("db:", StringComparison.OrdinalIgnoreCase);
        var parts = login.Split('@');
        var valid = serverUser
            ? login.Length > 3 && login[3..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            : parts.Length == 2 && parts.All(p => !string.IsNullOrWhiteSpace(p));
        if (login.Length > 256 || !valid || login.Any(char.IsControl))
            throw new DomainException("Podaj login serwera jako db:nazwa_uzytkownika (PostgreSQL) albo użytkownik@komputer (lokalny SQLite).");
        if (serverUser) login = "db:" + login[3..].ToLowerInvariant();
        var id = IdFor(login);
        if (await _db.Users.AnyAsync(u => u.Id == id || u.Login == login, ct))
            throw new DomainException("Operator już istnieje — zaznacz go i edytuj role.");
        var assigned = roles.Distinct().ToList();
        var user = new User
        {
            Id = id, Login = login, DisplayName = login, PasswordAlgorithm = SystemAccount,
            IsActive = true, CreatedAtUtc = _clock.UtcNow
        };
        user.SetRoles(assigned);
        _db.Users.Add(user);
        await new AuditService(_db, _clock).AddAsync(admin, "OPERATOR_DODANY_PRZEZ_ADMINISTRATORA", "User",
            id.ToString(), $"{login}; role={user.RolesCsv}", ct);
        await _db.SaveChangesAsync(ct);
        return new OperatorInfo(id, login, assigned, true, null, user.CreatedAtUtc);
    }

    /// <summary>Nadanie ról i aktywność operatora. Nie można odebrać roli ostatniemu aktywnemu administratorowi ani zmienić własnych ról.</summary>
    public async Task SetAsync(ICurrentUser admin, Guid operatorId, IEnumerable<Role> roles, bool active, CancellationToken ct = default)
    {
        admin.Require(Permission.ManageUsers);
        if (operatorId == admin.UserId) throw new DomainException("Nie można zmieniać własnych uprawnień — zrobi to inny administrator.");
        var user = await _db.Users.FirstAsync(u => u.Id == operatorId && u.PasswordAlgorithm == SystemAccount, ct);
        var list = roles.Distinct().ToList();
        var wasAdmin = user.IsActive && user.Roles.Contains(Role.Administrator);
        var staysAdmin = active && list.Contains(Role.Administrator);
        if (wasAdmin && !staysAdmin &&
            !await _db.Users.AnyAsync(u => u.Id != operatorId && u.PasswordAlgorithm == SystemAccount && u.IsActive && u.RolesCsv.Contains("Administrator"), ct))
            throw new DomainException("Nie można odebrać uprawnień ostatniemu aktywnemu administratorowi.");
        var before = $"{user.RolesCsv}; aktywny: {user.IsActive}";
        user.SetRoles(list);
        user.IsActive = active;
        await new AuditService(_db, _clock).AddAsync(admin, "OPERATOR_UPRAWNIENIA", "User", operatorId.ToString(),
            $"{user.Login}: {before} → {user.RolesCsv}; aktywny: {active}", ct);
        await _db.SaveChangesAsync(ct);
    }
}
