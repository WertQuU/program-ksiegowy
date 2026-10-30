using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Modules;

public class AccountWizardTests
{
    [Fact]
    public async Task Wizard_creates_mapped_analytic_accounts_from_plain_answers()
    {
        await using var t = await TestBooks.CreateAsync();
        var chart = await t.Db.Charts.AsNoTracking().Where(c => c.FiscalYearId == t.Year.Id).OrderByDescending(c => c.Version).FirstAsync();
        var w = new AccountWizard(t.Db, t.User, t.Clock);
        var a = await w.CreateAsync(chart.Id, "koszt-uslugi", "Ochrona obiektu", TaxAnswer.Tak);
        Assert.StartsWith("402-", a.Number);
        Assert.NotEqual("402-99", a.Number);
        Assert.Equal("R.B.III", a.ProfitLossLine);
        Assert.Equal(CitClassification.KosztPodatkowy, a.CitClassification);
        var b = await w.CreateAsync(chart.Id, "koszt-uslugi", "Catering na spotkania z klientami", TaxAnswer.Nie);
        Assert.Equal(CitClassification.KosztNiepodatkowy, b.CitClassification);
        Assert.NotEqual(a.Number, b.Number);
        var bank = await w.CreateAsync(chart.Id, "bank", "Rachunek w drugim banku", TaxAnswer.NieWiem);
        Assert.Equal("B.III.1.c", bank.BalanceDebitLine);
        var rev = await w.CreateAsync(chart.Id, "przychod-uslugi", "Usługi doradcze", TaxAnswer.NieWiem);
        Assert.Equal(CitClassification.DoAnalizy, rev.CitClassification);
        await Assert.ThrowsAsync<DomainException>(() => w.CreateAsync(chart.Id, "koszt-uslugi", " ", TaxAnswer.Tak));
    }
}
