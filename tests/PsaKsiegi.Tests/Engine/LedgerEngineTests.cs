using Microsoft.EntityFrameworkCore;
using PsaKsiegi.Application.Integrity;
using PsaKsiegi.Application.Ledger;
using PsaKsiegi.Application.Periods;
using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Ledger;
using PsaKsiegi.Domain.Periods;
using PsaKsiegi.Domain.Settlements;
using PsaKsiegi.Tests.Infrastructure;

namespace PsaKsiegi.Tests.Engine;

public class LedgerEngineTests
{
    private static readonly DateOnly Jan10 = new(2026, 1, 10);

    [Fact]
    public async Task Posting_assigns_continuous_numbers_and_hash_chain()
    {
        await using var t = await TestBooks.CreateAsync();
        var ledger = t.Ledger();
        var e1 = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 1000m, 0), new("801", 0, 1000m)));
        var e2 = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/2", new("402", 200m, 0), new("130-01", 0, 200m)));
        Assert.Null(e1.SequenceNumber);
        await ledger.PostAsync(e2.Id);
        await ledger.PostAsync(e1.Id);

        var posted = await t.Db.JournalEntries.AsNoTracking().Include(e => e.Lines).OrderBy(e => e.SequenceNumber).ToListAsync();
        Assert.Equal(new long?[] { 1, 2 }, posted.Select(p => p.SequenceNumber).ToArray());
        Assert.Equal("PK/2026/000001", posted[0].JournalNumber);
        Assert.Equal(e2.Id, posted[0].Id);
        Assert.Null(posted[0].PreviousHash);
        Assert.Equal(posted[0].Hash, posted[1].PreviousHash);
        Assert.All(posted, p => Assert.Equal(p.ComputeHash(), p.Hash));
        Assert.Equal(800m, await t.BalanceAsync("130-01"));
        Assert.Equal(-1000m, await t.BalanceAsync("801"));
        Assert.Empty(await new IntegrityService(t.Db, t.Clock).CheckLedgerChainAsync(t.Year.Id));
    }

    [Fact]
    public async Task Unbalanced_entry_is_rejected_with_difference()
    {
        await using var t = await TestBooks.CreateAsync();
        var ex = await Assert.ThrowsAsync<DomainException>(() => t.Ledger().CreateDraftAsync(
            TestBooks.Entry(Jan10, "PK/3", new("130-01", 100m, 0), new("801", 0, 99.99m))));
        Assert.Contains("nie bilansuje się", ex.Message);
        Assert.Contains("0,01", ex.Message);
    }

    [Theory]
    [InlineData("130-01", 10, 10, "tylko po jednej stronie")]
    [InlineData("130-01", 0, 0, "nie może być zerowa")]
    [InlineData("130-01", 10.001, 0, "2 miejsca")]
    [InlineData("130", 10, 0, "najniższym poziomie analityki")]
    [InlineData("200", 10, 0, "wymaga wskazania kontrahenta")]
    [InlineData("999", 10, 0, "nie istnieje")]
    public async Task Invalid_lines_are_rejected(string account, double debit, double credit, string expected)
    {
        await using var t = await TestBooks.CreateAsync();
        var d = (decimal)debit;
        var c = (decimal)credit;
        var ex = await Assert.ThrowsAsync<DomainException>(() => t.Ledger().CreateDraftAsync(
            TestBooks.Entry(Jan10, "PK/X", new(account, d, c), new("801", c, d))));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Negative_amounts_only_allowed_in_storno()
    {
        await using var t = await TestBooks.CreateAsync();
        var ex = await Assert.ThrowsAsync<DomainException>(() => t.Ledger().CreateDraftAsync(
            TestBooks.Entry(Jan10, "PK/N", new("130-01", -10m, 0), new("801", 0, -10m))));
        Assert.Contains("storno", ex.Message);
    }

    [Fact]
    public async Task Posted_entry_cannot_be_edited_or_deleted_by_service()
    {
        await using var t = await TestBooks.CreateAsync();
        var ledger = t.Ledger();
        var e = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 50m, 0), new("801", 0, 50m)));
        await ledger.PostAsync(e.Id);
        var ex = await Assert.ThrowsAsync<DomainException>(() => ledger.UpdateDraftAsync(e.Id,
            TestBooks.Entry(Jan10, "PK/1", new("130-01", 60m, 0), new("801", 0, 60m))));
        Assert.Contains("nie może być modyfikowany", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => ledger.DeleteDraftAsync(e.Id));
        await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(e.Id));
    }

    [Fact]
    public async Task Database_triggers_block_direct_modification_of_posted_entries()
    {
        await using var t = await TestBooks.CreateAsync();
        var ledger = t.Ledger();
        var e = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 50m, 0), new("801", 0, 50m)));
        await ledger.PostAsync(e.Id);
        var id = e.Id.ToString().ToUpperInvariant();
        var ex1 = await Assert.ThrowsAnyAsync<Exception>(() => t.Db.Database.ExecuteSqlRawAsync("UPDATE JournalEntries SET Description = 'zmiana' WHERE Id = {0}", id));
        Assert.Contains("nie moze byc zmieniony", ex1.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => t.Db.Database.ExecuteSqlRawAsync("UPDATE JournalLines SET Debit = '70' WHERE EntryId = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => t.Db.Database.ExecuteSqlRawAsync("DELETE FROM JournalEntries WHERE Id = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => t.Db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEvents"));
    }

    [Fact]
    public async Task Integrity_check_detects_tampering_when_triggers_are_bypassed()
    {
        await using var t = await TestBooks.CreateAsync();
        var ledger = t.Ledger();
        var e = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 50m, 0), new("801", 0, 50m)));
        await ledger.PostAsync(e.Id);
        // atak z pominięciem aplikacji: usunięcie wyzwalacza i zmiana kwoty
        await t.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER trg_jl_posted_no_update");
        await t.Db.Database.ExecuteSqlRawAsync("UPDATE JournalLines SET Debit = '5000.00' WHERE LineNo = 1");
        t.Db.ChangeTracker.Clear();
        var problems = await new IntegrityService(t.Db, t.Clock).CheckLedgerChainAsync(t.Year.Id);
        Assert.Contains(problems, p => p.Contains("niezgodna ze skrótem"));
        Assert.Contains(problems, p => p.Contains("nie bilansuje się"));
    }

    [Fact]
    public async Task Red_storno_keeps_original_and_zeroes_balances_without_inflating_turnover()
    {
        await using var t = await TestBooks.CreateAsync();
        var ledger = t.Ledger();
        var e = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("402", 300m, 0), new("130-01", 0, 300m)));
        await ledger.PostAsync(e.Id);
        var rev = await ledger.ReverseAsync(e.Id, "Błędne konto kosztowe");

        Assert.Equal(EntryKind.Storno, rev.Kind);
        Assert.Equal(e.Id, rev.ReversesEntryId);
        Assert.All(rev.Lines, l => Assert.True(l.Debit <= 0 && l.Credit <= 0));
        var original = await t.Db.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstAsync(x => x.Id == e.Id);
        Assert.Equal(rev.Id, original.ReversedByEntryId);
        Assert.Equal(300m, original.TotalDebit);
        Assert.Equal(0m, await t.BalanceAsync("402"));
        var tb = await new LedgerQueries(t.Db).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        Assert.Equal(0m, tb.First(x => x.Account.Number == "402").CumulativeDebit); // storno czerwone nie zawyża obrotów
        await Assert.ThrowsAsync<DomainException>(() => ledger.ReverseAsync(e.Id, "ponownie"));
        await Assert.ThrowsAsync<DomainException>(() => ledger.ReverseAsync(rev.Id, "storno storna"));
    }

    [Fact]
    public async Task Correction_creates_storno_and_corrected_entry_linked_to_original()
    {
        await using var t = await TestBooks.CreateAsync();
        var ledger = t.Ledger();
        var e = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("402", 300m, 0), new("130-01", 0, 300m)));
        await ledger.PostAsync(e.Id);
        var (rev, corr) = await ledger.CorrectAsync(e.Id, "Koszt dotyczy zużycia materiałów",
            TestBooks.Entry(Jan10, "PK/1", new("401", 300m, 0), new("130-01", 0, 300m)));
        Assert.Equal(EntryKind.Korekta, corr.Kind);
        Assert.Equal(e.Id, corr.CorrectsEntryId);
        Assert.Equal(0m, await t.BalanceAsync("402"));
        Assert.Equal(300m, await t.BalanceAsync("401"));
        Assert.Equal(-300m, await t.BalanceAsync("130-01"));
        Assert.Equal(3, await t.Db.JournalEntries.CountAsync(x => x.Status == EntryStatus.Zaksiegowany));
        Assert.Empty(await new IntegrityService(t.Db, t.Clock).CheckLedgerChainAsync(t.Year.Id));
    }

    [Fact]
    public async Task Black_storno_reverses_sides()
    {
        await using var t = await TestBooks.CreateAsync();
        var policy = await t.Db.AccountingPolicies.FirstAsync();
        policy.CorrectionMethod = Domain.Companies.CorrectionMethod.StornoCzarne;
        await t.Db.SaveChangesAsync();
        var ledger = t.Ledger();
        var e = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("402", 300m, 0), new("130-01", 0, 300m)));
        await ledger.PostAsync(e.Id);
        var rev = await ledger.ReverseAsync(e.Id, "test");
        Assert.Equal(300m, rev.Lines.Single(l => l.AccountNumber == "402").Credit);
        var tb = await new LedgerQueries(t.Db).TrialBalanceAsync(t.Year.Id, t.Year.StartDate, t.Year.EndDate);
        var a402 = tb.First(x => x.Account.Number == "402");
        Assert.Equal(300m, a402.CumulativeDebit);
        Assert.Equal(300m, a402.CumulativeCredit);
        Assert.Equal(0m, a402.Balance);
    }

    [Fact]
    public async Task Posting_is_blocked_until_policy_is_approved()
    {
        await using var t = await TestBooks.CreateAsync(approvePolicy: false);
        var ledger = t.Ledger();
        var e = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 50m, 0), new("801", 0, 50m)));
        var ex = await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(e.Id));
        Assert.Contains("polityka rachunkowości", ex.Message);
    }

    [Fact]
    public async Task Posting_is_blocked_until_chart_is_approved()
    {
        await using var t = await TestBooks.CreateAsync(approveChart: false);
        var e = await t.Ledger().CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 50m, 0), new("801", 0, 50m)));
        var ex = await Assert.ThrowsAsync<DomainException>(() => t.Ledger().PostAsync(e.Id));
        Assert.Contains("plan kont", ex.Message);
    }

    [Fact]
    public async Task Data_persists_after_restart()
    {
        await using var t = await TestBooks.CreateAsync();
        var e = await t.Ledger().CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 1234.56m, 0), new("801", 0, 1234.56m)));
        await t.Ledger().PostAsync(e.Id);
        await t.ReopenAsync();
        Assert.Equal(1234.56m, await t.BalanceAsync("130-01"));
        var reloaded = await t.Db.JournalEntries.Include(x => x.Lines).FirstAsync();
        Assert.Equal(reloaded.ComputeHash(), reloaded.Hash);
    }

    [Fact]
    public async Task Settlement_lines_open_and_settle_items_with_partial_payments()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Odbiorca sp. z o.o.");
        var ledger = t.Ledger();
        var inv = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "FS/1/2026", new("200", 1230m, 0, cp.Id), new("700-01", 0, 1000m), new("221-01", 0, 230m)));
        await ledger.PostAsync(inv.Id);
        var item = await t.Db.OpenItems.SingleAsync();
        Assert.Equal(OpenItemSide.Naleznosc, item.Side);
        Assert.Equal(1230m, item.AmountPln);

        var pay1 = await ledger.CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 20), "WB/1", new("130-01", 500m, 0), new("200", 0, 500m, cp.Id, OpenItemId: item.Id)));
        await ledger.PostAsync(pay1.Id);
        await t.Db.Entry(item).ReloadAsync();
        Assert.Equal(OpenItemStatus.CzesciowoRozliczona, item.Status);
        Assert.Equal(730m, item.RemainingPln);

        var over = await ledger.CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 21), "WB/2", new("130-01", 800m, 0), new("200", 0, 800m, cp.Id, OpenItemId: item.Id)));
        var ex = await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(over.Id));
        Assert.Contains("przekracza", ex.Message);
        t.Db.ChangeTracker.Clear();
        await ledger.DeleteDraftAsync(over.Id);

        var pay2 = await ledger.CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 22), "WB/3", new("130-01", 730m, 0), new("200", 0, 730m, cp.Id, OpenItemId: item.Id)));
        await ledger.PostAsync(pay2.Id);
        item = await t.Db.OpenItems.AsNoTracking().SingleAsync();
        Assert.Equal(OpenItemStatus.Rozliczona, item.Status);
        Assert.Equal(0m, await t.BalanceAsync("200"));
        Assert.Equal(2, await t.Db.SettlementAllocations.CountAsync());
        Assert.Empty(await new IntegrityService(t.Db, t.Clock).CheckOpenItemsAgainstLedgerAsync(t.Year.Id, t.Year.EndDate));
    }

    [Fact]
    public async Task Storno_of_invoice_with_payment_is_rejected_until_payment_reversed()
    {
        await using var t = await TestBooks.CreateAsync();
        var cp = await t.AddCounterpartyAsync("K001", "Odbiorca");
        var ledger = t.Ledger();
        var inv = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "FS/1", new("200", 100m, 0, cp.Id), new("700-01", 0, 100m)));
        await ledger.PostAsync(inv.Id);
        var item = await t.Db.OpenItems.SingleAsync();
        var pay = await ledger.CreateDraftAsync(TestBooks.Entry(Jan10, "WB/1", new("130-01", 40m, 0), new("200", 0, 40m, cp.Id, OpenItemId: item.Id)));
        await ledger.PostAsync(pay.Id);
        await Assert.ThrowsAsync<DomainException>(() => ledger.ReverseAsync(inv.Id, "anulowanie"));
        t.Db.ChangeTracker.Clear();
        await ledger.ReverseAsync(pay.Id, "zwrot wpłaty");
        await ledger.ReverseAsync(inv.Id, "anulowanie");
        var oi = await t.Db.OpenItems.AsNoTracking().SingleAsync();
        Assert.Equal(0m, oi.AmountPln);
        Assert.Equal(0m, oi.SettledAmountPln);
        Assert.Equal(0m, await t.BalanceAsync("200"));
    }

    [Fact]
    public async Task Closed_period_blocks_posting_and_reopen_is_controlled()
    {
        await using var t = await TestBooks.CreateAsync();
        var periods = new PeriodService(t.Db, t.User, t.Clock);
        var jan = t.Year.Periods.Single(p => p.Number == 1);
        var feb = t.Year.Periods.Single(p => p.Number == 2);

        await Assert.ThrowsAsync<DomainException>(() => periods.ClosePeriodAsync(feb.Id, "x")); // styczeń otwarty
        var checklist = await periods.ClosePeriodAsync(jan.Id, null);
        Assert.False(checklist.HasBlocking);
        var e = await t.Ledger().CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 31), "PK/9", new("130-01", 10m, 0), new("801", 0, 10m)));
        var ex = await Assert.ThrowsAsync<DomainException>(() => t.Ledger().PostAsync(e.Id));
        Assert.Contains("zamknięty", ex.Message);

        await periods.ClosePeriodAsync(feb.Id, null);
        await Assert.ThrowsAsync<DomainException>(() => periods.ReopenPeriodAsync(jan.Id, "Korekta faktury styczniowej")); // najpierw luty
        await Assert.ThrowsAsync<DomainException>(() => periods.ReopenPeriodAsync(feb.Id, "krótko"));
        await periods.ReopenPeriodAsync(feb.Id, "Korekta faktury lutowej po kontroli");
        await periods.ReopenPeriodAsync(jan.Id, "Korekta faktury styczniowej po kontroli");
        t.Db.ChangeTracker.Clear();
        await t.Ledger().PostAsync(e.Id);
        Assert.Equal(2, await t.Db.PeriodStatusChanges.CountAsync(c => c.ToStatus == PeriodStatus.Otwarty));
        Assert.Equal(1, (await t.Db.Periods.FirstAsync(p => p.Id == jan.Id)).ReopenCount);
    }

    [Fact]
    public async Task Close_period_with_draft_is_blocked()
    {
        await using var t = await TestBooks.CreateAsync();
        await t.Ledger().CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 10m, 0), new("801", 0, 10m)));
        var periods = new PeriodService(t.Db, t.User, t.Clock);
        var ex = await Assert.ThrowsAsync<DomainException>(() => periods.ClosePeriodAsync(t.Year.Periods.Single(p => p.Number == 1).Id, "uzasadnienie"));
        Assert.Contains("buforze", ex.Message);
    }

    [Fact]
    public async Task Posting_outside_period_of_operation_is_rejected()
    {
        await using var t = await TestBooks.CreateAsync();
        var input = new EntryInput("PK", new DateOnly(2026, 2, 5), new DateOnly(2026, 2, 5), Jan10, "Operacja lutowa w styczniu", "PK", "PK/1",
            new LineInput[] { new("130-01", 10m, 0), new("801", 0, 10m) });
        var e = await t.Ledger().CreateDraftAsync(input);
        var ex = await Assert.ThrowsAsync<DomainException>(() => t.Ledger().PostAsync(e.Id));
        Assert.Contains("ująć w okresie", ex.Message);
    }

    [Fact]
    public async Task Audit_log_records_postings_and_chain_verifies()
    {
        await using var t = await TestBooks.CreateAsync();
        var e = await t.Ledger().CreateDraftAsync(TestBooks.Entry(Jan10, "PK/1", new("130-01", 10m, 0), new("801", 0, 10m)));
        await t.Ledger().PostAsync(e.Id);
        Assert.True(await t.Db.AuditEvents.AnyAsync(a => a.Action == "ZAPIS_ZAKSIEGOWANY" && a.EntityId == e.Id.ToString()));
        Assert.Empty(await new Application.Audit.AuditService(t.Db, t.Clock).VerifyChainAsync());
    }
}
