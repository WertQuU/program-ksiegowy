using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Documents;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Application.Security;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Documents;
using PelnaKsiegowosc.Domain.Payroll;
using PelnaKsiegowosc.Domain.Security;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class OctoberLegalAndWorkstationAuditTests
{
    [Fact]
    public void Early_invoice_does_not_advance_ordinary_vat_to_issue_month()
    {
        var invoice = new Document
        {
            Kind = DocumentKind.FakturaSprzedazy,
            IssueDate = new DateOnly(2026, 1, 20),
            OperationDate = new DateOnly(2026, 2, 10)
        };
        Assert.Equal(new DateOnly(2026, 2, 1), DocumentService.DefaultVatPeriod(invoice));
    }

    [Fact]
    public async Task Employment_started_before_2019_cannot_be_erased_after_only_ten_years()
    {
        await using var t = await TestBooks.CreateAsync();
        var payroll = new PayrollService(t.Db, t.User, t.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Historyczna", LastName = "Osoba",
            ContractType = ContractType.UmowaOPrace, Gross = 3000,
            StartDate = new DateOnly(2008, 1, 1), EndDate = new DateOnly(2015, 6, 30)
        });
        var decision = await new PersonalDataService(t.Db, t.User, t.Clock).EmployeeRetentionAsync(employee.Id);
        Assert.False(decision.CanErase);
        Assert.Equal(new DateOnly(2065, 6, 30), decision.KeepUntil);
    }

    [Fact]
    public async Task Employment_started_after_2018_retains_to_end_of_tenth_calendar_year()
    {
        await using var t = await TestBooks.CreateAsync();
        var payroll = new PayrollService(t.Db, t.User, t.Clock);
        var employee = await payroll.AddEmployeeAsync(new Employee
        {
            FirstName = "Nowsza", LastName = "Osoba",
            ContractType = ContractType.UmowaOPrace, Gross = 3000,
            StartDate = new DateOnly(2019, 1, 1), EndDate = new DateOnly(2022, 2, 1)
        });
        var decision = await new PersonalDataService(t.Db, t.User, t.Clock).EmployeeRetentionAsync(employee.Id);
        Assert.False(decision.CanErase);
        Assert.Equal(new DateOnly(2032, 12, 31), decision.KeepUntil);
    }

    [Fact]
    public async Task New_workstation_does_not_get_admin_merely_by_connecting_to_existing_books()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new OperatorAccessService(t.Db, t.Clock);
        var login = "newuser@NEWPC";
        var roles = await service.ResolveAsync(OperatorAccessService.IdFor(login), login, t.Company.Id);
        Assert.Empty(roles);
    }

    [Fact]
    public async Task Administrator_can_pre_register_workstation_with_narrow_roles()
    {
        await using var t = await TestBooks.CreateAsync();
        var service = new OperatorAccessService(t.Db, t.Clock);
        var login = "accountant@LAPTOP";
        var registered = await service.RegisterAsync(t.User, login, new[] { Role.Ksiegowy });
        Assert.Equal(OperatorAccessService.IdFor(login), registered.Id);
        Assert.Equal(new[] { Role.Ksiegowy },
            await service.ResolveAsync(registered.Id, login, t.Company.Id));
        await Assert.ThrowsAsync<DomainException>(() => service.RegisterAsync(t.User, login, new[] { Role.Administrator }));
    }
}
