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
/// Uprawnienia operatorów. W trybie centralnym tożsamością jest indywidualny login PostgreSQL
/// rzeczywiście używany do uwierzytelnienia. Role aplikacyjne zapisane są w bazie.
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

    /// <summary>Stały identyfikator operatora wynikający z loginu.</summary>
    public static Guid IdFor(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) throw new DomainException("Podaj login operatora.");
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

    /// <summary>
    /// Odmowa dostępu dla nieprzydzielonego loginu bazy.
    /// Nie tworzymy kont przy logowaniu ani nie aktualizujemy tabeli Users —
    /// operatorzy z ograniczonymi uprawnieniami SQL nie potrzebują DML do tabeli z rolami.
    /// </summary>
    public async Task<IReadOnlyList<Role>> ResolveAsync(Guid operatorId, string login, Guid companyId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == operatorId, ct);
        if (user is null || !string.Equals(user.Login, login, StringComparison.Ordinal) ||
            user.PasswordAlgorithm != SystemAccount || !user.IsActive || user.Roles.Count == 0)
            throw new DomainException("Login PostgreSQL nie został dopuszczony do ksiąg albo został zablokowany. Poproś administratora o dodanie loginu i nadanie uprawnień.");
        return user.Roles;
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
        var valid = serverUser && login.Length > 3 && login[3..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
        if (login.Length > 256 || !valid || login.Any(char.IsControl))
            throw new DomainException("Podaj login PostgreSQL w formacie db:nazwa_uzytkownika.");
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
