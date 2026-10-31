using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Capital;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public sealed class PsaCapitalPostingTests
{
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
            SolvencyTestConfirmed = true
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
