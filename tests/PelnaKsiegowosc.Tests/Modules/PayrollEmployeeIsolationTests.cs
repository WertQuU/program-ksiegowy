using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

/// <summary>Zmiany w kadrach nie mogą przepinać powiązanych kontrahentów między jednostkami.</summary>
public class PayrollEmployeeIsolationTests
{
    [Fact]
    public async Task Editing_employee_cannot_relink_counterparty_from_another_company()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Jan", LastName = "Nowak", ContractType = ContractType.UmowaOPrace,
            Gross = 6000m, StartDate = new DateOnly(2026, 1, 1)
        });

        // Druga jednostka w bazie jest przygotowana bezpośrednio przez EF.
        // Interfejs standardowo obsługuje jedną jednostkę, ale izolacja usług
        // nie może na tym polegać.
        var otherCompany = new Company
        {
            FullName = "Druga spółka", ShortName = "Druga", Nip = "1234567802",
            LegalForm = LegalForm.ProstaSpolkaAkcyjna
        };
        books.Db.Companies.Add(otherCompany);
        var otherCounterparty = new Counterparty
        {
            CompanyId = otherCompany.Id, Code = "EXT01", Name = "Dane obcej jednostki",
            Kind = CounterpartyKind.OsobaFizyczna
        };
        books.Db.Counterparties.Add(otherCounterparty);
        await books.Db.SaveChangesAsync();

        var changed = await books.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        var ownCounterpartyId = changed.CounterpartyId;
        changed.CounterpartyId = otherCounterparty.Id;
        changed.FirstName = "Nieuprawniona";
        var error = await Assert.ThrowsAsync<DomainException>(() => payroll.UpdateEmployeeAsync(changed));
        Assert.Contains("powiązanego kontrahenta", error.Message);

        books.Db.ChangeTracker.Clear();
        var storedEmployee = await books.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        var storedExternal = await books.Db.Counterparties.AsNoTracking().SingleAsync(c => c.Id == otherCounterparty.Id);
        Assert.Equal(ownCounterpartyId, storedEmployee.CounterpartyId);
        Assert.Equal("Jan", storedEmployee.FirstName);
        Assert.Equal("Dane obcej jednostki", storedExternal.Name);
        Assert.Equal(otherCompany.Id, storedExternal.CompanyId);
    }

    [Fact]
    public async Task Editing_employee_keeps_system_fields_and_updates_only_own_counterparty()
    {
        await using var books = await TestBooks.CreateAsync();
        var payroll = new PayrollService(books.Db, books.User, books.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Anna", LastName = "Kowal", ContractType = ContractType.UmowaOPrace,
            Gross = 5000m, StartDate = new DateOnly(2026, 1, 1)
        });
        var changed = await books.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        var linked = changed.CounterpartyId;
        var originalCreatedAt = changed.CreatedAtUtc;

        // Formularz może nie przekazać CompanyId. Nie pozwalamy też
        // nadpisywać daty utworzenia danych identyfikacyjnych.
        changed.CompanyId = Guid.Empty;
        changed.CreatedAtUtc = DateTime.UnixEpoch;
        changed.LastName = "Nowak";
        await payroll.UpdateEmployeeAsync(changed);

        books.Db.ChangeTracker.Clear();
        var stored = await books.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        var cp = await books.Db.Counterparties.AsNoTracking().SingleAsync(c => c.Id == linked);
        Assert.Equal(books.Company.Id, stored.CompanyId);
        Assert.Equal(linked, stored.CounterpartyId);
        Assert.Equal(originalCreatedAt, stored.CreatedAtUtc);
        Assert.Equal("Anna Nowak", cp.Name);
        Assert.Equal(books.Company.Id, cp.CompanyId);
    }
}
