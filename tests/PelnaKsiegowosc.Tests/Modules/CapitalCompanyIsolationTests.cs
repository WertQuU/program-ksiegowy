using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Capital;
using PelnaKsiegowosc.Domain.Capital;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Przynależność właścicieli i ich rozrachunków do jednej jednostki.</summary>
public sealed class CapitalCompanyIsolationTests
{
    private static readonly DateOnly Date = new(2026, 1, 10);

    private static async Task<(Company company, Counterparty counterparty)> AddForeignCounterpartyAsync(TestBooks books)
    {
        var company = new Company
        {
            FullName = "Inna jednostka", ShortName = "Inna", Nip = "1234567802"
        };
        books.Db.Companies.Add(company);
        var counterparty = new Counterparty
        {
            CompanyId = company.Id, Code = "FOREIGN", Name = "Kontrahent obcej jednostki"
        };
        books.Db.Counterparties.Add(counterparty);
        await books.Db.SaveChangesAsync();
        return (company, counterparty);
    }

    [Fact]
    public async Task Cannot_create_capital_event_for_shareholder_of_another_company()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new CapitalService(books.Db, books.User, books.Clock);
        var (company, cp) = await AddForeignCounterpartyAsync(books);
        var shareholder = new Shareholder
        {
            CompanyId = company.Id, CounterpartyId = cp.Id, Name = "Obcy akcjonariusz"
        };
        books.Db.Shareholders.Add(shareholder);
        await books.Db.SaveChangesAsync();

        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(new CapitalEvent
            {
                Type = CapitalEventType.WplataWkladu,
                EventDate = Date, ShareholderId = shareholder.Id, TotalAmount = 100m
            }));
        Assert.Contains("aktualnej jednostki", denied.Message);

        await books.AddCounterpartyAsync("NEXT", "Zapis po błędzie");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.CapitalEvents
            .AnyAsync(e => e.ShareholderId == shareholder.Id));
    }

    [Fact]
    public async Task Cannot_post_capital_event_with_foreign_counterparty_relinked_to_owner()
    {
        await using var books = await TestBooks.CreateAsync();
        var service = new CapitalService(books.Db, books.User, books.Clock);
        var shareholder = await service.AddShareholderAsync("Akcjonariusz testowy", null, false);
        var ev = await service.CreateEventAsync(new CapitalEvent
        {
            Type = CapitalEventType.WplataWkladu,
            EventDate = Date, ShareholderId = shareholder.Id, TotalAmount = 100m
        });
        await service.ConfirmDocumentsAsync(ev.Id);

        var (_, foreignCp) = await AddForeignCounterpartyAsync(books);
        shareholder.CounterpartyId = foreignCp.Id;
        await books.Db.SaveChangesAsync();

        var denied = await Assert.ThrowsAsync<DomainException>(() => service.PostEventAsync(ev.Id));
        Assert.Contains("aktualnej jednostki", denied.Message);

        books.Db.ChangeTracker.Clear();
        Assert.Equal(CapitalEventStatus.DokumentyPotwierdzone,
            (await books.Db.CapitalEvents.SingleAsync(e => e.Id == ev.Id)).Status);
        Assert.False(await books.Db.JournalEntries
            .AnyAsync(e => e.SourceDocumentId == ev.DocumentId && ev.DocumentId != null));
    }
}
