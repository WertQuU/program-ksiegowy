using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Banking;
using PelnaKsiegowosc.Domain.Banking;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Settlements;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Negatywne scenariusze autoryzacji i rozliczeń kasowo-bankowych.</summary>
public sealed class BankScopeAndAllocationSafetyTests
{
    [Fact]
    public async Task Cash_operation_rejects_foreign_company_counterparty_without_creating_transaction()
    {
        await using var books = await TestBooks.CreateAsync();
        var bank = new BankService(books.Db, books.User, books.Clock);
        var cash = await bank.CreateMoneyAccountAsync(MoneyAccountKind.Kasa, "Kasa", "100", null);

        var foreign = new Company { FullName = "Inna jednostka", ShortName = "Inna", Nip = "1234567802" };
        books.Db.Companies.Add(foreign);
        var external = new Counterparty
        {
            CompanyId = foreign.Id, Code = "FOREIGN", Name = "Nieuprawniony kontrahent"
        };
        books.Db.Counterparties.Add(external);
        await books.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            bank.AddCashOperationAsync(cash.Id, new DateOnly(2026, 1, 10),
                100m, "Wpłata", external.Id));
        Assert.Contains("aktualnej jednostce", error.Message);

        await books.AddCounterpartyAsync("LATER", "Zapis po odmowie");
        books.Db.ChangeTracker.Clear();
        Assert.False(await books.Db.MoneyTransactions.AnyAsync(t => t.CompanyId == books.Company.Id));
    }

    [Fact]
    public async Task Repeating_same_open_item_in_multiple_allocations_cannot_overpay()
    {
        await using var books = await TestBooks.CreateAsync();
        var cp = await books.AddCounterpartyAsync("C001", "Odbiorca");
        var ledger = books.Ledger();
        var invoice = await ledger.CreateDraftAsync(TestBooks.Entry(
            new DateOnly(2026, 1, 10), "FS/100",
            new("200", 100m, 0m, cp.Id), new("700-01", 0m, 100m)));
        await ledger.PostAsync(invoice.Id);
        var item = await books.Db.OpenItems.SingleAsync();

        var bank = new BankService(books.Db, books.User, books.Clock);
        var cash = await bank.CreateMoneyAccountAsync(MoneyAccountKind.Kasa, "Kasa", "100", null);
        var payment = await bank.AddCashOperationAsync(cash.Id, new DateOnly(2026, 1, 11),
            150m, "Wpłata zbiorcza", cp.Id);

        // Każdy składnik 60 zł mieści się w saldzie 100 zł, ale suma 120 zł już nie.
        var rejected = await Assert.ThrowsAsync<DomainException>(() => bank.MatchAsync(payment.Id,
            new[] { new Allocation(item.Id, 60m), new Allocation(item.Id, 60m) }, "139"));
        Assert.Contains("Łączna kwota rozliczenia", rejected.Message);

        books.Db.ChangeTracker.Clear();
        var saved = await books.Db.MoneyTransactions.SingleAsync(t => t.Id == payment.Id);
        Assert.Equal(TransactionStatus.Nierozliczona, saved.Status);
        Assert.Equal(100m, (await books.Db.OpenItems.SingleAsync(o => o.Id == item.Id)).Remaining);
    }
}
