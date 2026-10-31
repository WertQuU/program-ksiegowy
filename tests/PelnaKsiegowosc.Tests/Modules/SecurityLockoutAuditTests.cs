using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Security;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Regresja bezpieczeństwa logowania: próby TOTP nie mogą omijać blokady konta.</summary>
public class SecurityLockoutAuditTests
{
    [Fact]
    public async Task Five_invalid_mfa_codes_lock_account_even_when_password_is_correct()
    {
        await using var t = await TestBooks.CreateAsync();
        var protector = new DataProtector(RandomNumberGenerator.GetBytes(32));
        var svc = new SecurityService(t.Db, t.Clock, protector, passwordIterations: 10_000);
        const string password = "Secure#LongPassword123";
        var (user, _) = await svc.CreateUserAsync(t.User, "mfatester", "MFA Tester",
            password, new[] { Role.Administrator });
        var actor = new CurrentUser(user.Id, user.Login, t.Company.Id, new[] { Role.Administrator });
        var (secretBase32, _) = await svc.BeginMfaEnrollmentAsync(actor);
        var secret = Totp.FromBase32(secretBase32);
        var correct = Totp.Compute(secret, new DateTimeOffset(t.Clock.UtcNow).ToUnixTimeSeconds());
        await svc.ConfirmMfaAsync(actor, correct);

        var invalid = correct == "000000" ? "999999" : "000000";
        for (var i = 0; i < SecurityService.MaxFailedAttempts; i++)
        {
            var attempt = await svc.LoginAsync(user.Login, password, invalid);
            Assert.False(attempt.Success);
        }
        var locked = await t.Db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.NotNull(locked.LockedUntilUtc);
        Assert.True(locked.LockedUntilUtc > t.Clock.UtcNow);

        var validButLocked = await svc.LoginAsync(user.Login, password, correct);
        Assert.False(validButLocked.Success);
        Assert.Contains("zablokowane", validButLocked.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
