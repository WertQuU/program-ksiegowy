using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Engine;

/// <summary>Systemowe rodzaje dekretów nie mogą być podrabiane w zwykłym buforze.</summary>
public sealed class LedgerReservedKindsAndDraftReferencesTests
{
    private static readonly DateOnly Day = new(2026, 1, 10);

    [Theory]
    [InlineData(EntryKind.Korekta)]
    [InlineData(EntryKind.BilansOtwarcia)]
    [InlineData(EntryKind.Zamkniecie)]
    public async Task User_cannot_create_edit_or_post_reserved_journal_kind(EntryKind kind)
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var normal = TestBooks.Entry(Day, "PK/ORDINARY",
            new("130-01", 100m, 0m), new("801", 0m, 100m));
        var forged = normal with { Kind = kind, DocumentNumber = "PK/FORGED" };

        await Assert.ThrowsAsync<DomainException>(() => ledger.CreateDraftAsync(forged));
        await Assert.ThrowsAsync<DomainException>(() => ledger.CreateAndPostAsync(forged));
        var original = await ledger.CreateDraftAsync(normal);
        await Assert.ThrowsAsync<DomainException>(() => ledger.UpdateDraftAsync(original.Id, forged));

        // Ochrona obowiązuje też, gdy zawartość bufora zostanie zmieniona
        // poza interfejsem użytkownika, jeszcze przed jego zaksięgowaniem.
        original.Kind = kind;
        await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(original.Id));
        original.Kind = EntryKind.Zwykly;
        await ledger.PostAsync(original.Id);
        Assert.True(original.IsPosted);

        books.Db.ChangeTracker.Clear();
        var entries = await books.Db.JournalEntries.AsNoTracking().ToListAsync();
        Assert.Single(entries);
        Assert.Equal(EntryKind.Zwykly, entries[0].Kind);
        Assert.Equal("PK/ORDINARY", entries[0].DocumentNumber);
    }

    [Fact]
    public async Task Draft_edit_with_foreign_counterparty_is_rejected_before_changing_tracked_lines()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var original = await ledger.CreateDraftAsync(TestBooks.Entry(Day, "PK/ORIGINAL",
            new("130-01", 100m, 0m), new("801", 0m, 100m)));

        var foreign = new Company
        {
            FullName = "Obca jednostka", ShortName = "Obca", Nip = "1234567802"
        };
        books.Db.Companies.Add(foreign);
        var cp = new Counterparty
        {
            CompanyId = foreign.Id, Code = "FOR", Name = "Kontrahent obcej jednostki"
        };
        books.Db.Counterparties.Add(cp);
        await books.Db.SaveChangesAsync();

        var attempted = TestBooks.Entry(Day, "PK/FORGED",
            new("130-01", 120m, 0m, cp.Id), new("801", 0m, 120m));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            ledger.UpdateDraftAsync(original.Id, attempted));
        Assert.Contains("innej jednostki", error.Message);

        // Następne SaveChanges w tym samym kontekście nie może utrwalić
        // odrzuconego dekretu ani powiązania z obcym kontrahentem.
        await ledger.CreateDraftAsync(TestBooks.Entry(Day, "PK/NEXT",
            new("130-01", 20m, 0m), new("801", 0m, 20m)));

        books.Db.ChangeTracker.Clear();
        var stored = await books.Db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .SingleAsync(e => e.Id == original.Id);
        Assert.Equal("PK/ORIGINAL", stored.DocumentNumber);
        Assert.Equal(100m, stored.TotalDebit);
        Assert.Equal(100m, stored.TotalCredit);
        Assert.All(stored.Lines, l => Assert.Null(l.CounterpartyId));
        Assert.Null(stored.ModifiedAtUtc);
    }
}
