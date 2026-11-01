using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Engine;

/// <summary>Nieudana dekretacja bez bufora nie może pozostawić niezatwierdzonego wpisu Added.</summary>
public sealed class DirectPostingMutationSafetyTests
{
    private static readonly DateOnly Day = new(2026, 1, 10);

    [Fact]
    public async Task Unapproved_policy_rejects_direct_post_without_leaking_ghost_draft()
    {
        await using var books = await TestBooks.CreateAsync(approvePolicy: false);
        var ledger = books.Ledger();
        var input = TestBooks.Entry(Day, "PK/POLICY-DENIED",
            new("130-01", 100m, 0m), new("801", 0m, 100m));

        await Assert.ThrowsAsync<DomainException>(() => ledger.CreateAndPostAsync(input));

        // Inny zapis w tym samym DbContext nie może utrwalić nieudanego dekretu.
        await books.AddCounterpartyAsync("AFTER-POLICY", "Po odmowie z powodu polityki");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.JournalEntries.AsNoTracking()
            .AnyAsync(e => e.DocumentNumber == "PK/POLICY-DENIED"));
    }

    [Fact]
    public async Task Closed_period_rejects_direct_post_without_leaking_ghost_draft()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var period = books.Year.Periods.Single(p => p.Contains(Day));
        period.Status = PeriodStatus.Zamkniety;
        await books.Db.SaveChangesAsync();

        var input = TestBooks.Entry(Day, "PK/CLOSED-DENIED",
            new("130-01", 100m, 0m), new("801", 0m, 100m));
        await Assert.ThrowsAsync<DomainException>(() => ledger.CreateAndPostAsync(input));

        await books.AddCounterpartyAsync("AFTER-CLOSED", "Po odmowie w zamkniętym okresie");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.JournalEntries.AsNoTracking()
            .AnyAsync(e => e.DocumentNumber == "PK/CLOSED-DENIED"));
    }
}
