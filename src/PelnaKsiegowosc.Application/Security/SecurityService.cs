using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Audit;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Application.Security;

public sealed record LoginResult(bool Success, User? User, bool MfaRequired, string? Error);

/// <summary>
/// Konta lokalne, logowanie, blokada po nieudanych próbach, MFA (TOTP), kody odzyskiwania.
/// Brak „uniwersalnego hasła administratora”: dostęp odzyskuje się kodem odzyskiwania lub
/// przez innego administratora (reset hasła z wymuszeniem zmiany).
/// </summary>
public sealed class SecurityService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly KsiegiDbContext _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly DataProtector? _protector;
    private readonly int _iterations;

    public SecurityService(KsiegiDbContext db, IClock clock, DataProtector? protector, int passwordIterations = PasswordHasher.DefaultIterations)
    {
        _db = db;
        _clock = clock;
        _audit = new AuditService(db, clock);
        _protector = protector;
        _iterations = passwordIterations;
    }

    public Task<bool> AnyUserExistsAsync(CancellationToken ct = default) => _db.Users.AnyAsync(ct);

    /// <summary>Tworzy użytkownika. Zwraca jednorazowe kody odzyskiwania (pokazywane tylko raz).</summary>
    public async Task<(User user, IReadOnlyList<string> recoveryCodes)> CreateUserAsync(ICurrentUser? actor, string login, string displayName,
        string password, IEnumerable<Role> roles, CancellationToken ct = default)
    {
        var isFirst = !await _db.Users.AnyAsync(ct);
        if (!isFirst)
        {
            if (actor is null) throw new AccessDeniedException("Tworzenie kolejnych użytkowników wymaga zalogowanego administratora.");
            actor.Require(Permission.ManageUsers);
        }
        login = (login ?? "").Trim();
        if (login.Length < 3 || login.Length > 64 || !login.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new DomainException("Login: 3–64 znaki (litery, cyfry, . _ -).");
        if (await _db.Users.AnyAsync(u => u.Login == login, ct)) throw new DomainException($"Użytkownik „{login}” już istnieje.");
        PasswordHasher.ValidatePolicy(password, login).ThrowIfInvalid();
        var roleList = roles.Distinct().ToList();
        if (roleList.Count == 0) throw new DomainException("Wybierz co najmniej jedną rolę.");
        if (isFirst && !roleList.Contains(Role.Administrator)) roleList.Add(Role.Administrator);

        var (hash, salt, it) = PasswordHasher.Hash(password, _iterations);
        var user = new User
        {
            Login = login,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? login : displayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = it,
            PasswordChangedAtUtc = _clock.UtcNow,
            CreatedAtUtc = _clock.UtcNow
        };
        user.SetRoles(roleList);
        _db.Users.Add(user);
        var codes = AddRecoveryCodes(user.Id);
        await _audit.AddAsync(actor, "UZYTKOWNIK_UTWORZONY", "User", user.Id.ToString(), $"login={login}; role={user.RolesCsv}", ct);
        await _db.SaveChangesAsync(ct);
        return (user, codes);
    }

    private List<string> AddRecoveryCodes(Guid userId)
    {
        var existing = _db.RecoveryCodes.Where(r => r.UserId == userId && r.UsedAtUtc == null).ToList();
        foreach (var e in existing) e.UsedAtUtc = _clock.UtcNow; // unieważnienie starych
        var codes = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var code = FormatCode(RandomNumberGenerator.GetBytes(6));
            codes.Add(code);
            _db.RecoveryCodes.Add(new RecoveryCode { UserId = userId, CodeHash = HashCode(code), CreatedAtUtc = _clock.UtcNow });
        }
        return codes;
    }

    private static string FormatCode(byte[] b)
    {
        var s = Convert.ToHexString(b);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("-", "").ToUpperInvariant())));

    public async Task<LoginResult> LoginAsync(string login, string password, string? totpCode, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Login == login, ct);
        if (user is null)
        {
            // stała praca dla nieistniejącego konta (utrudnia enumerację)
            PasswordHasher.Verify(password, Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[16]), Math.Min(_iterations, 10_000));
            await _audit.AddAsync(null, "LOGOWANIE_NIEUDANE", "User", null, $"login={Sanitize(login)} (nieznany)", ct);
            await _db.SaveChangesAsync(ct);
            return new(false, null, false, "Nieprawidłowy login lub hasło.");
        }
        if (!user.IsActive) return new(false, null, false, "Konto jest nieaktywne.");
        if (user.LockedUntilUtc is { } lu && lu > _clock.UtcNow)
            return new(false, null, false, $"Konto zablokowane do {lu.ToPlDateTime()} po {MaxFailedAttempts} nieudanych próbach.");

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockedUntilUtc = _clock.UtcNow.Add(LockoutDuration);
                user.FailedLoginCount = 0;
            }
            await _audit.AddAsync(null, "LOGOWANIE_NIEUDANE", "User", user.Id.ToString(), $"login={user.Login}", ct);
            await _db.SaveChangesAsync(ct);
            return new(false, null, false, "Nieprawidłowy login lub hasło.");
        }

        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(totpCode)) return new(false, null, true, "Wymagany kod z aplikacji uwierzytelniającej.");
            if (!VerifyMfa(user, totpCode))
            {
                // Błędny drugi składnik musi podlegać takiej samej blokadzie jak błędne hasło.
                // W przeciwnym razie po ujawnieniu hasła możliwe są nieograniczone próby TOTP.
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= MaxFailedAttempts)
                {
                    user.LockedUntilUtc = _clock.UtcNow.Add(LockoutDuration);
                    user.FailedLoginCount = 0;
                }
                await _audit.AddAsync(null, "MFA_NIEUDANE", "User", user.Id.ToString(),
                    user.LockedUntilUtc > _clock.UtcNow ? "Przekroczono limit prób MFA — konto tymczasowo zablokowane" : null, ct);
                await _db.SaveChangesAsync(ct);
                return new(false, null, true, "Nieprawidłowy kod MFA.");
            }
        }

        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        user.LastLoginAtUtc = _clock.UtcNow;
        await _audit.AddAsync(new CurrentUser(user.Id, user.Login, Guid.Empty, user.Roles), "LOGOWANIE", "User", user.Id.ToString(), null, ct);
        await _db.SaveChangesAsync(ct);
        return new(true, user, false, null);
    }

    private bool VerifyMfa(User user, string code)
    {
        if (_protector is null || user.MfaSecretProtected is null) return false;
        var secret = _protector.Unprotect(user.MfaSecretProtected, "mfa:" + user.Id);
        return Totp.Verify(secret, code.Trim(), _clock.UtcNow);
    }

    /// <summary>Rozpoczyna konfigurację MFA — zwraca sekret (Base32) i URI do kodu QR. MFA aktywuje się po potwierdzeniu kodem.</summary>
    public async Task<(string base32, string uri)> BeginMfaEnrollmentAsync(ICurrentUser actor, CancellationToken ct = default)
    {
        if (_protector is null) throw new DomainException("Brak klucza szyfrowania bazy — MFA niedostępne.");
        var user = await _db.Users.FirstAsync(u => u.Id == actor.UserId, ct);
        var secret = Totp.GenerateSecret();
        user.MfaSecretProtected = _protector.Protect(secret, "mfa:" + user.Id);
        user.MfaEnabled = false;
        await _db.SaveChangesAsync(ct);
        return (Totp.ToBase32(secret), Totp.ProvisioningUri("Pełna Księgowość", user.Login, secret));
    }

    public async Task ConfirmMfaAsync(ICurrentUser actor, string code, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == actor.UserId, ct);
        if (!VerifyMfa(user, code)) throw new DomainException("Kod nieprawidłowy — MFA nie zostało włączone.");
        user.MfaEnabled = true;
        await _audit.AddAsync(actor, "MFA_WLACZONE", "User", user.Id.ToString(), null, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(ICurrentUser actor, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == actor.UserId, ct);
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
            throw new DomainException("Bieżące hasło jest nieprawidłowe.");
        SetPassword(user, newPassword);
        user.MustChangePassword = false;
        await _audit.AddAsync(actor, "HASLO_ZMIENIONE", "User", user.Id.ToString(), null, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Reset hasła innego użytkownika przez administratora — wymusza zmianę przy logowaniu, wyłącza MFA.</summary>
    public async Task AdminResetPasswordAsync(ICurrentUser admin, Guid userId, string temporaryPassword, CancellationToken ct = default)
    {
        admin.Require(Permission.ManageUsers);
        if (admin.UserId == userId) throw new DomainException("Nie można resetować własnego hasła tą funkcją — użyj zmiany hasła.");
        var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);
        SetPassword(user, temporaryPassword);
        user.MustChangePassword = true;
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        user.LockedUntilUtc = null;
        await _audit.AddAsync(admin, "HASLO_ZRESETOWANE", "User", user.Id.ToString(), $"login={user.Login}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Odzyskanie dostępu kodem odzyskiwania (jednorazowym) — ustawia nowe hasło i wyłącza MFA.</summary>
    public async Task RecoverWithCodeAsync(string login, string recoveryCode, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Login == login, ct) ?? throw new DomainException("Nieprawidłowe dane odzyskiwania.");
        var h = HashCode(recoveryCode);
        var code = await _db.RecoveryCodes.FirstOrDefaultAsync(r => r.UserId == user.Id && r.CodeHash == h && r.UsedAtUtc == null, ct);
        if (code is null)
        {
            await _audit.AddAsync(null, "ODZYSKIWANIE_NIEUDANE", "User", user.Id.ToString(), null, ct);
            await _db.SaveChangesAsync(ct);
            throw new DomainException("Nieprawidłowe dane odzyskiwania.");
        }
        code.UsedAtUtc = _clock.UtcNow;
        SetPassword(user, newPassword);
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        user.LockedUntilUtc = null;
        user.FailedLoginCount = 0;
        await _audit.AddAsync(new CurrentUser(user.Id, user.Login, Guid.Empty, user.Roles), "DOSTEP_ODZYSKANY", "User", user.Id.ToString(), "kod odzyskiwania", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(ICurrentUser actor, CancellationToken ct = default)
    {
        var codes = AddRecoveryCodes(actor.UserId);
        await _audit.AddAsync(actor, "KODY_ODZYSKIWANIA_ODNOWIONE", "User", actor.UserId.ToString(), null, ct);
        await _db.SaveChangesAsync(ct);
        return codes;
    }

    public async Task SetActiveAsync(ICurrentUser admin, Guid userId, bool active, CancellationToken ct = default)
    {
        admin.Require(Permission.ManageUsers);
        var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!active && user.Roles.Contains(Role.Administrator) &&
            !await _db.Users.AnyAsync(u => u.Id != userId && u.IsActive && u.RolesCsv.Contains("Administrator"), ct))
            throw new DomainException("Nie można dezaktywować ostatniego aktywnego administratora.");
        user.IsActive = active;
        await _audit.AddAsync(admin, active ? "UZYTKOWNIK_AKTYWOWANY" : "UZYTKOWNIK_DEZAKTYWOWANY", "User", userId.ToString(), user.Login, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetRolesAsync(ICurrentUser admin, Guid userId, IEnumerable<Role> roles, CancellationToken ct = default)
    {
        admin.Require(Permission.ManageUsers);
        var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);
        var list = roles.Distinct().ToList();
        if (list.Count == 0) throw new DomainException("Wybierz co najmniej jedną rolę.");
        if (user.Roles.Contains(Role.Administrator) && !list.Contains(Role.Administrator) &&
            !await _db.Users.AnyAsync(u => u.Id != userId && u.IsActive && u.RolesCsv.Contains("Administrator"), ct))
            throw new DomainException("Nie można odebrać roli ostatniemu administratorowi.");
        user.SetRoles(list);
        await _audit.AddAsync(admin, "ROLE_ZMIENIONE", "User", userId.ToString(), user.RolesCsv, ct);
        await _db.SaveChangesAsync(ct);
    }

    private void SetPassword(User user, string password)
    {
        PasswordHasher.ValidatePolicy(password, user.Login).ThrowIfInvalid();
        var (hash, salt, it) = PasswordHasher.Hash(password, _iterations);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = it;
        user.PasswordChangedAtUtc = _clock.UtcNow;
    }

    private static string Sanitize(string s) => new((s ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray());
}
