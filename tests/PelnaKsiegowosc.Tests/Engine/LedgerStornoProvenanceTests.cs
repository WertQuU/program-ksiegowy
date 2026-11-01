using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Engine;

/// <summary>Storno musi odnosić się do rzeczywistego zaksięgowanego zapisu.</summary>
public sealed class LedgerStornoProvenanceTests
{
    private static readonly DateOnly Day = new(2026, 1, 10);

    [Fact]
    public async Task Client_cannot_inject_negative_storno_via_draft_or_direct_posting()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var fake = TestBooks.Entry(Day, "PK/FAKE",
            new("130-01", -100m, 0m), new("801", 0m, -100m)) with { Kind = EntryKind.Storno };
        var draftError = await Assert.ThrowsAsync<DomainException>(() => ledger.CreateDraftAsync(fake));
        Assert.Contains("funkcję korekty", draftError.Message);
        var directError = await Assert.ThrowsAsync<DomainException>(() => ledger.CreateAndPostAsync(fake));
        Assert.Contains("funkcję korekty", directError.Message);
        Assert.False(await books.Db.JournalEntries.AnyAsync());
    }

    [Fact]
    public async Task Draft_cannot_be_retyped_as_storno_to_bypass_validation()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var standard = TestBooks.Entry(Day, "PK/REG",
            new("130-01", 100m, 0m), new("801", 0m, 100m));
        var draft = await ledger.CreateDraftAsync(standard);

        var forbiddenEdit = await Assert.ThrowsAsync<DomainException>(() =>
            ledger.UpdateDraftAsync(draft.Id, standard with { Kind = EntryKind.Storno }));
        Assert.Contains("funkcję korekty", forbiddenEdit.Message);

        // Chronimy także zapis roboczy o Kind zmienionym poza usługą dekretacji.
        draft.Kind = EntryKind.Storno;
        var forbiddenPost = await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(draft.Id));
        Assert.Contains("funkcję korekty", forbiddenPost.Message);
        draft.Kind = EntryKind.Zwykly;
        await ledger.PostAsync(draft.Id);
        Assert.True(draft.IsPosted);

        // Legalna korekta tworzy storno ze śladem pochodzenia.
        var reversal = await ledger.ReverseAsync(draft.Id, "Poprawka księgowania");
        Assert.Equal(EntryKind.Storno, reversal.Kind);
        Assert.Equal(draft.Id, reversal.ReversesEntryId);
        Assert.Equal(reversal.Id, draft.ReversedByEntryId);
        Assert.Equal(EntryStatus.Zaksiegowany, reversal.Status);
    }
}
