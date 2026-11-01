using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Engine;

public class LedgerCrossCompanyReferenceTests
{
    private static readonly DateOnly Date = new(2026, 1, 10);

    [Fact]
    public async Task Foreign_counterparty_is_rejected_for_draft_and_direct_posting()
    {
        await using var books = await TestBooks.CreateAsync();
        var company = new Company { FullName = "Druga jednostka", ShortName = "Druga", Nip = "1234567802" };
        books.Db.Companies.Add(company);
        var external = new Counterparty
        {
            CompanyId = company.Id, Name = "Kontrahent innej jednostki", Code = "X01"
        };
        books.Db.Counterparties.Add(external);
        await books.Db.SaveChangesAsync();

        var ledger = books.Ledger();
        var input = TestBooks.Entry(Date, "PK/FOREIGN",
            new("130-01", 100m, 0, external.Id), new("801", 0, 100m));
        var draft = await Assert.ThrowsAsync<DomainException>(() => ledger.CreateDraftAsync(input));
        Assert.Contains("innej jednostki", draft.Message);
        var direct = await Assert.ThrowsAsync<DomainException>(() => ledger.CreateAndPostAsync(input));
        Assert.Contains("innej jednostki", direct.Message);
        Assert.False(await books.Db.JournalEntries.AnyAsync());
    }

    [Fact]
    public async Task Counterparty_membership_is_verified_again_at_posting()
    {
        await using var books = await TestBooks.CreateAsync();
        var own = await books.AddCounterpartyAsync("OWN", "Własny kontrahent");
        var ledger = books.Ledger();
        var draft = await ledger.CreateDraftAsync(TestBooks.Entry(Date, "PK/OWN",
            new("130-01", 100m, 0, own.Id), new("801", 0, 100m)));
        draft.Lines.Single(l => l.CounterpartyId.HasValue).CounterpartyId = Guid.NewGuid();

        var blocked = await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(draft.Id));
        Assert.Contains("kontrahenta", blocked.Message);
        Assert.Null(draft.SequenceNumber);
        Assert.Equal(EntryStatus.Bufor, draft.Status);
    }

    [Fact]
    public async Task Foreign_open_item_cannot_be_settled_even_with_matching_account_and_counterparty()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var own = await books.AddCounterpartyAsync("OWN", "Własny kontrahent");
        // Prawdziwy zapis zaksięgowany dostarcza istniejącego klucza JournalEntryId.
        var seed = await ledger.CreateDraftAsync(TestBooks.Entry(Date, "PK/SEED",
            new("130-01", 100m, 0), new("801", 0, 100m)));
        await ledger.PostAsync(seed.Id);

        var other = new Company { FullName = "Druga jednostka", ShortName = "Druga", Nip = "1234567802" };
        books.Db.Companies.Add(other);
        var settlementAccount = await books.Db.Accounts.AsNoTracking().FirstAsync(a => a.Number == "200");
        var external = new OpenItem
        {
            CompanyId = other.Id,
            AccountId = settlementAccount.Id, AccountNumber = settlementAccount.Number,
            CounterpartyId = own.Id, JournalEntryId = seed.Id,
            Side = OpenItemSide.Naleznosc,
            Reference = "OBCY/1", DocumentDate = Date, Amount = 100m, AmountPln = 100m
        };
        books.Db.OpenItems.Add(external);
        await books.Db.SaveChangesAsync();

        var attempt = await ledger.CreateDraftAsync(TestBooks.Entry(Date, "PK/SETTLE",
            new("130-01", 100m, 0),
            new("200", 0, 100m, own.Id, OpenItemId: external.Id)));
        var blocked = await Assert.ThrowsAsync<DomainException>(() => ledger.PostAsync(attempt.Id));
        Assert.Contains("nie należy do tej jednostki", blocked.Message);
        Assert.Equal(0m, external.SettledAmount);
    }
}
