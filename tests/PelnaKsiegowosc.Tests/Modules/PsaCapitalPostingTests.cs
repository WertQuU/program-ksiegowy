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
        ev.SolvencyTestNotes = System.Text.Json.JsonSerializer.Serialize(new PsaPayoutAssessment
        {
            ApprovedFinancialStatementReference = "SF/2025", SixMonthForecastReference = "PROG/2026/01",
            LastYearProfit = 4000m, ShareCapitalBefore = 1000m,
            LiabilitiesInApprovedStatement = 100000m, NonDistributableReserveAllocation = 320m,
            MinimumProjectedLiquidityAfterPayout = 200m, SixMonthSolvencyConfirmed = true
        });
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
            SolvencyTestNotes = System.Text.Json.JsonSerializer.Serialize(new PsaPayoutAssessment
        {
            ApprovedFinancialStatementReference = "SF/2025", SixMonthForecastReference = "PROG/2026/01",
            LastYearProfit = 4000m, ShareCapitalBefore = 1000m,
            LiabilitiesInApprovedStatement = 100000m, NonDistributableReserveAllocation = 320m,
            MinimumProjectedLiquidityAfterPayout = 200m, SixMonthSolvencyConfirmed = true
        })
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
    [Fact]
    public void Psa_payout_rejects_distributable_limit_and_mandatory_capital_allocation_violations()
    {
        var ev = new CapitalEvent { Type = CapitalEventType.UchwalenieDywidendy, TotalAmount = 500m,
            SolvencyTestConfirmed = true, ResolutionReference = "WZA/1/2026" };
        var bad = new PsaPayoutAssessment
        {
            ApprovedFinancialStatementReference = "SF/2025",
            SixMonthForecastReference = "PROG/2026/01",
            LastYearProfit = 200m, ShareCapitalBefore = 1000m,
            LiabilitiesInApprovedStatement = 100000m,
            NonDistributableReserveAllocation = 0m,
            MinimumProjectedLiquidityAfterPayout = 100m,
            SixMonthSolvencyConfirmed = true
        };
        var errors = bad.Problems(ev);
        Assert.Contains(errors, e => e.Contains("300¹⁵"));
        Assert.Contains(errors, e => e.Contains("300¹⁹"));
        bad.LastYearProfit = 1000m;
        bad.NonDistributableReserveAllocation = 80m;
        Assert.Empty(bad.Problems(ev));
    }

    [Fact]
    public void Capital_reduction_uses_share_capital_amount_when_total_amount_is_zero()
    {
        var ev = new CapitalEvent
        {
            Type = CapitalEventType.ObnizenieKapitaluAkcyjnego,
            ShareCapitalAmount = 1200m, TotalAmount = 0m,
            KrsEntryReference = "KRS/123", ResolutionReference = "WZA/2026/2",
            SolvencyTestConfirmed = true
        };
        var assessment = new PsaPayoutAssessment
        {
            ApprovedFinancialStatementReference = "SF/2025",
            SixMonthForecastReference = "PL/2026",
            ShareCapitalBefore = 2000m,
            LiabilitiesInApprovedStatement = 0m,
            ShareCapitalApprovedForDistribution = 500m,
            MinimumProjectedLiquidityAfterPayout = 100m,
            SixMonthSolvencyConfirmed = true,
            CapitalReductionRegisteredInKrs = true,
            CreditorProtectionRequirementsConfirmed = true
        };
        var errors = assessment.Problems(ev);
        Assert.Contains(errors, e => e.Contains("1200.00"));
        Assert.Contains(errors, e => e.Contains("nie pokrywa całego obniżenia"));
    }

}
