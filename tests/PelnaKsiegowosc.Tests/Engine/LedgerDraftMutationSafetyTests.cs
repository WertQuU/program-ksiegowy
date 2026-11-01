using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Engine;

public class LedgerDraftMutationSafetyTests
{
    private static readonly DateOnly Date = new(2026, 1, 10);

    [Theory]
    [InlineData("unbalanced")]
    [InlineData("unknown-account")]
    public async Task Rejected_draft_update_does_not_change_tracked_or_persisted_original(string kind)
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var original = await ledger.CreateDraftAsync(TestBooks.Entry(Date, "PK/ORIG",
            new("130-01", 100m, 0m), new("801", 0m, 100m)));
        var input = kind == "unknown-account"
            ? TestBooks.Entry(Date, "PK/BAD", new("999", 100m, 0m), new("801", 0m, 100m))
            : TestBooks.Entry(Date, "PK/BAD", new("130-01", 100m, 0m), new("801", 0m, 99m));
        await Assert.ThrowsAsync<DomainException>(() => ledger.UpdateDraftAsync(original.Id, input));

        // Nie tylko stan po wyjątku — także późniejszy SaveChanges tej samej instancji
        // DbContext nie może przypadkiem utrwalić żadnej części odrzuconej zmiany.
        await ledger.CreateDraftAsync(TestBooks.Entry(Date, "PK/INNY",
            new("130-01", 200m, 0m), new("801", 0m, 200m)));

        books.Db.ChangeTracker.Clear();
        var persisted = await books.Db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .SingleAsync(e => e.Id == original.Id);
        Assert.Equal(EntryStatus.Bufor, persisted.Status);
        Assert.Equal("PK/ORIG", persisted.DocumentNumber);
        Assert.Equal(2, persisted.Lines.Count);
        Assert.Equal(100m, persisted.TotalDebit);
        Assert.Equal(100m, persisted.TotalCredit);
        Assert.Null(persisted.ModifiedAtUtc);

        // Oryginalny, poprawny bufor można dalej księgować.
        await ledger.PostAsync(original.Id);
        Assert.Equal(EntryStatus.Zaksiegowany,
            (await books.Db.JournalEntries.AsNoTracking().SingleAsync(e => e.Id == original.Id)).Status);
    }
}
