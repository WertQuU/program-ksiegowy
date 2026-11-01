using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Engine;

/// <summary>Storno i zapis poprawny są jedną nierozdzielną korektą.</summary>
public sealed class LedgerCorrectionAtomicityTests
{
    private static readonly DateOnly Day = new(2026, 1, 10);

    [Fact]
    public async Task Invalid_correction_cannot_storno_original_and_leak_into_later_save()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var original = await ledger.CreateDraftAsync(TestBooks.Entry(Day, "PK/ORIG",
            new("130-01", 100m, 0m), new("801", 0m, 100m)));
        await ledger.PostAsync(original.Id);

        var invalid = TestBooks.Entry(Day, "PK/INVALID",
            new("130-01", 120m, 0m), new("NIEISTNIEJACE", 0m, 120m));
        await Assert.ThrowsAsync<DomainException>(() =>
            ledger.CorrectAsync(original.Id, "Prawidłowe wyjaśnienie korekty", invalid));

        // Każda następna operacja zapisująca ten sam kontekst musi zachować
        // pierwotny dekret oraz brak osieroconego storna.
        await ledger.CreateDraftAsync(TestBooks.Entry(Day, "PK/LATER",
            new("130-01", 50m, 0m), new("801", 0m, 50m)));
        books.Db.ChangeTracker.Clear();
        var posted = await books.Db.JournalEntries.AsNoTracking()
            .Where(x => x.Status == EntryStatus.Zaksiegowany).ToListAsync();
        Assert.Single(posted);
        Assert.Equal(original.Id, posted[0].Id);
        Assert.Null(posted[0].ReversedByEntryId);
    }

    [Fact]
    public async Task Failed_settlement_in_replacement_rolls_back_reversal_and_allows_retry()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var owner = await books.AddCounterpartyAsync("OWN", "Kontrahent");
        var original = await ledger.CreateDraftAsync(TestBooks.Entry(Day, "PK/ORIG",
            new("130-01", 100m, 0m), new("801", 0m, 100m)));
        await ledger.PostAsync(original.Id);

        // Jest to błąd wykrywany dopiero przy zastosowaniu rozrachunków,
        // po próbie zbudowania storna. Musi wycofać OBIE części korekty.
        var invalidSettlement = TestBooks.Entry(Day, "PK/SETTLE",
            new("130-01", 100m, 0m), new("200", 0m, 100m, owner.Id, OpenItemId: Guid.NewGuid()));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            ledger.CorrectAsync(original.Id, "Korekta z rozrachunkiem", invalidSettlement));
        Assert.Contains("rozrachunek", error.Message);

        await books.AddCounterpartyAsync("NEXT", "Po odmowie korekty");
        books.Db.ChangeTracker.Clear();
        var afterFailure = await books.Db.JournalEntries.AsNoTracking()
            .Where(e => e.Status == EntryStatus.Zaksiegowany).ToListAsync();
        Assert.Single(afterFailure);
        Assert.Null(afterFailure.Single().ReversedByEntryId);

        var corrected = TestBooks.Entry(Day, "PK/GOOD",
            new("130-01", 120m, 0m), new("801", 0m, 120m));
        var (storno, replacement) = await ledger.CorrectAsync(original.Id,
            "Poprawna korekta po wcześniejszej odmowie", corrected);
        Assert.Equal(EntryKind.Storno, storno.Kind);
        Assert.Equal(EntryKind.Korekta, replacement.Kind);
        Assert.Equal(original.Id, replacement.CorrectsEntryId);
        Assert.Equal(original.Id, storno.ReversesEntryId);
        Assert.Equal(storno.Id,
            (await books.Db.JournalEntries.AsNoTracking().SingleAsync(e => e.Id == original.Id)).ReversedByEntryId);
    }

    [Fact]
    public async Task Reversal_rejected_by_closed_period_must_not_persist_as_ghost_draft()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var original = await ledger.CreateDraftAsync(TestBooks.Entry(Day, "PK/REV",
            new("130-01", 100m, 0m), new("801", 0m, 100m)));
        await ledger.PostAsync(original.Id);

        var period = books.Year.Periods.Single(p => p.Contains(Day));
        period.Status = PelnaKsiegowosc.Domain.Periods.PeriodStatus.Zamkniety;
        await books.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            ledger.ReverseAsync(original.Id, "Powód storna", postingDate: Day));
        Assert.Contains("zamknięty", error.Message);

        // Kolejny poprawny SaveChanges na tym samym kontekście nie może
        // utrwalić odrzuconego storna ani zmienić wskaźnika oryginału.
        await books.AddCounterpartyAsync("LATER", "Po nieudanym stornie");
        books.Db.ChangeTracker.Clear();
        var entries = await books.Db.JournalEntries.AsNoTracking().ToListAsync();
        Assert.Single(entries);
        Assert.Equal(original.Id, entries[0].Id);
        Assert.Null(entries[0].ReversedByEntryId);
        Assert.Equal(EntryStatus.Zaksiegowany, entries[0].Status);
    }
}
