using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Settlements;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Noty kompensacyjne i dokumenty źródłowe muszą zatwierdzać się atomowo.</summary>
public sealed class CompensationRollbackTests
{
    [Fact]
    public async Task Failure_updating_document_status_rolls_back_compensation_journal_and_settlements()
    {
        await using var books = await TestBooks.CreateAsync();
        var cp = await books.AddCounterpartyAsync("COMP-ATOMIC", "Kontrahent wzajemny");
        await SalesAndBankScenarioTests.IssueAndPostSalesInvoiceAsync(
            books, cp.Id, 1000m, new DateOnly(2026, 1, 12));
        await PurchaseScenarioTests.RegisterPurchaseAsync(
            books, cp.Id, 400m, "FV/COMP-ATOMIC", 100m);

        var receivable = await books.Db.OpenItems.SingleAsync(x => x.Side == OpenItemSide.Naleznosc);
        var payable = await books.Db.OpenItems.SingleAsync(x => x.Side == OpenItemSide.Zobowiazanie);
        var entriesBefore = await books.Db.JournalEntries.CountAsync();
        var recBefore = receivable.RemainingPln;
        var payBefore = payable.RemainingPln;

        // Błąd dopiero w końcowej aktualizacji statusu dokumentu:
        // nota kompensacyjna i jej rozrachunki muszą wycofać się razem.
        await books.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER "test_fail_payment_status"
            BEFORE UPDATE ON "Documents"
            BEGIN
                SELECT RAISE(ABORT, 'injected document status failure');
            END;
            """);

        var settlements = new SettlementService(books.Db, books.User, books.Clock);
        await Assert.ThrowsAnyAsync<Exception>(() => settlements.CompensateAsync(
            receivable.Id, payable.Id, 492m, new DateOnly(2026, 1, 31), "NK/ROLLBACK"));

        await books.Db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER \"test_fail_payment_status\"");
        await books.AddCounterpartyAsync("COMP-LATER", "Następna czynność po odmowie");
        books.Db.ChangeTracker.Clear();

        Assert.Equal(entriesBefore, await books.Db.JournalEntries.CountAsync());
        Assert.Empty(await books.Db.SettlementAllocations.ToListAsync());
        Assert.Equal(recBefore, (await books.Db.OpenItems.AsNoTracking()
            .SingleAsync(x => x.Id == receivable.Id)).RemainingPln);
        Assert.Equal(payBefore, (await books.Db.OpenItems.AsNoTracking()
            .SingleAsync(x => x.Id == payable.Id)).RemainingPln);
    }
}
