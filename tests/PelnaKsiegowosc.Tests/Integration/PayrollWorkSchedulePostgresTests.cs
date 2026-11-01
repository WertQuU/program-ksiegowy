using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Payroll;
using PelnaKsiegowosc.Infrastructure.Backup;
using PelnaKsiegowosc.Tests.Infrastructure;
using PelnaKsiegowosc.Tests.Modules;

namespace PelnaKsiegowosc.Tests.Integration;

public class PayrollWorkSchedulePostgresTests
{
    [PostgresFact]
    public async Task Confirmed_schedule_survives_import_and_produces_same_payroll_on_postgres()
    {
        await using var source = await TestBooks.CreateAsync();
        var (service, list, _, days) = await PayrollWorkScheduleTests.PrepareAsync(source);
        var itemId = list.Items.Single().Id;
        await service.ConfirmWorkScheduleAsync(list.Id, itemId, days, "GRAFIK/03");
        await service.RecalculateAsync(list.Id);
        var expected = list.Items.Single();
        var dump = Path.Combine(source.Directory, "dump");
        await DataDump.ExportAsync(source.Db, dump);
        var (pg, _, _) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var target = pg;
        await pg.Database.MigrateAsync();
        await DataDump.ImportAsync(pg, dump);
        var migrated = new PayrollService(pg, source.User, source.Clock);
        var recalculated = await migrated.RecalculateAsync(list.Id);
        var actual = recalculated.Items.Single();
        Assert.Equal(expected.SocialBasis, actual.SocialBasis);
        Assert.Equal(expected.SickPay, actual.SickPay);
        Assert.Equal(expected.Health, actual.Health);
        Assert.Equal(expected.TaxAdvance, actual.TaxAdvance);
        Assert.Equal(expected.Net, actual.Net);
        await migrated.PostAsync(list.Id);
        Assert.NotNull(recalculated.JournalEntryId);
    }
}
