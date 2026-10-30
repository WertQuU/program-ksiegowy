using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Security;

public sealed record OperatorInfo(Guid Id, string Login, IReadOnlyList<Role> Roles, bool IsActive, DateTime? LastSeenUtc, DateTime CreatedAtUtc);

/// <summary>
/// Uprawnienia operatorów. Program nie ma własnych haseł (ani hasła uniwersalnego) — operatora identyfikuje konto systemu
/// operacyjnego (login@komputer), a w trybie serwerowym dodatkowo konto PostgreSQL. Role każdego operatora są zapisane
/// w bazie: pierwszy operator, który zakłada księgi, otrzymuje role administratora, głównego księgowego i zarządu;
/// każdy kolejny operator zaczyna bez uprawnień i otrzymuje role od administratora (rozdział obowiązków). Wszystkie
/// zmiany ról trafiają do dziennika zdarzeń.
/// </summary>
public sealed class OperatorAccessService
{
    /// <summary>Oznaczenie kont operatorów systemowych w tabeli użytkowników (bez hasła).</summary>
    public const string SystemAccount = "SYSTEM";
    public static readonly Role[] FounderRoles = { Role.Administrator, Role.GlownyKsiegowy, Role.Zarzad };

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
        var anyOperator = await _db.Users.AnyAsync(u => u.PasswordAlgorithm == SystemAccount, ct);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == operatorId, ct);
        if (user is null)
        {
            user = new User
            {
                Id = operatorId, Login = login, DisplayName = login, PasswordAlgorithm = SystemAccount, IsActive = true, CreatedAtUtc = _clock.UtcNow
            };
            if (!anyOperator) user.SetRoles(FounderRoles);
            _db.Users.Add(user);
            var actor = new CurrentUser(operatorId, login, companyId, user.Roles);
            await new AuditService(_db, _clock).AddAsync(actor, anyOperator ? "OPERATOR_NOWY_BEZ_UPRAWNIEN" : "OPERATOR_ZALOZYCIEL",
                "User", operatorId.ToString(), anyOperator ? $"{login}: oczekuje na nadanie ról przez administratora" : $"{login}: {user.RolesCsv}", ct);
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
