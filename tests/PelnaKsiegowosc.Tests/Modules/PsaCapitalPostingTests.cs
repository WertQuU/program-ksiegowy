using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Capital;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public sealed class PsaCapitalPostingTests
{
    [Fact]
    public async Task Psa_dividend_decision_requires_documented_distributable_amount_and_six_month_forecast()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new CapitalService(t.Db, t.User, t.Clock);
        var owner = await service.AddShareholderAsync("Akcjonariusz", null, false);
        var ev = await service.CreateEventAsync(new CapitalEvent
        {
            Type = CapitalEventType.UchwalenieDywidendy,
            EventDate = new DateOnly(2026, 1, 20),
            TotalAmount = 200m,
            ShareholderId = owner.Id,
            ResolutionReference = "WZA/1/2026",
            SolvencyTestConfirmed = true
        });
        var missing = await Assert.ThrowsAsync<DomainException>(() => service.ConfirmDocumentsAsync(ev.Id));
        Assert.Contains("P.S.A.", missing.Message);
        ev.SolvencyTestNotes = "Kontrola limitu dywidendy, kapitału i płynności na sześć miesięcy: analiza FIN/2026/1.";
        await t.Db.SaveChangesAsync();
        await service.ConfirmDocumentsAsync(ev.Id);
        Assert.Equal(CapitalEventStatus.DokumentyPotwierdzone, ev.Status);
    }

    [Theory]
    [InlineData("solvency")]
    [InlineData("resolution")]
    [InlineData("tax")]
    public async Task Posting_rechecks_psa_payout_requirements_after_documents_were_confirmed(string changed)
    {
        await using var t = await TestBooks.CreateAsync(); // P.S.A.
        var service = new CapitalService(t.Db, t.User, t.Clock);
        var shareholder = await service.AddShareholderAsync("Jan Akcjonariusz", null, false);
        var ev = await service.CreateEventAsync(new CapitalEvent
        {
            Type = CapitalEventType.ZaliczkaNaPoczetDywidendy,
            EventDate = new DateOnly(2026, 1, 20), TotalAmount = 500m,
            ShareholderId = shareholder.Id, ResolutionReference = "Uchwała 1/2026 i umowa spółki",
            SolvencyTestConfirmed = true,
            SolvencyTestNotes = "Wyliczenie kwoty do podziału i prognoza wypłacalności: arkusz PSA/2026/01, okres 6 miesięcy."
        });
        await service.ConfirmDocumentsAsync(ev.Id);
        // Stare/importowane dane albo inna ścieżka edycji nie mogą ominąć kontroli księgowania.
        if (changed == "solvency") ev.SolvencyTestConfirmed = false;
        if (changed == "resolution") ev.ResolutionReference = null;
        if (changed == "tax") ev.WithholdingTaxAmount = 501m;
        await t.Db.SaveChangesAsync();
        var count = await t.Db.JournalEntries.CountAsync();
        var error = await Assert.ThrowsAsync<DomainException>(() => service.PostEventAsync(ev.Id));
        Assert.Contains("P.S.A.", error.Message);
        Assert.Equal(count, await t.Db.JournalEntries.CountAsync());
        Assert.Null(ev.JournalEntryId);
        Assert.Equal(CapitalEventStatus.DokumentyPotwierdzone, ev.Status);
    }
}
