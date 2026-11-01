using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Periods;
using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Ledger;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public class YearClosingRollbackTests
{
    [Fact]
    public async Task Failed_opening_balance_rolls_back_closed_year_and_allows_retry_in_same_context()
    {
        await using var books = await TestBooks.CreateAsync();
        var ledger = books.Ledger();
        var service = new PeriodService(books.Db, books.User, books.Clock);
        var fiscalYearId = books.Year.Id;

        var income = await ledger.CreateDraftAsync(TestBooks.Entry(new DateOnly(2026, 1, 10),
            "PK/INCOME", new("130-01", 100m, 0m), new("700-01", 0m, 100m)));
        await ledger.PostAsync(income.Id);

        foreach (var period in books.Year.Periods.OrderBy(p => p.Number))
            await service.ClosePeriodAsync(period.Id, "Uzgodnienie okresu wykonane — test");

        await new FinancialStatementApprovals(books.Db, books.User, books.Clock)
            .RecordApprovalAsync("2026", books.Clock.Today, "Walne Zgromadzenie", "Uchwała 1/2027");

        // Konto rozliczenia wyniku finansowego nie jest badane w checklist
        // zamknięcia, a jest wymagane dopiero przy bilansie otwarcia nowego roku.
        var settings = new SettingsService(books.Db, books.Clock);
        var posting = await settings.GetAsync(books.Company.Id, PostingSettings.SettingKey,
            () => new PostingSettings());
        posting.RetainedEarnings = "NIEISTNIEJACE-BO";
        await settings.SetAsync(books.Company.Id, PostingSettings.SettingKey, posting);
        await books.Db.SaveChangesAsync();

        var preview = await service.PreviewYearCloseAsync(fiscalYearId);
        Assert.False(preview.Checklist.HasBlocking);
        Assert.NotEqual(0m, preview.NetResult);

        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            service.CloseYearAsync(fiscalYearId, "ZAMKNIJ 2026",
                "Wyniki uzgodnione do testu odrzucenia bilansu otwarcia"));
        Assert.Contains("NIEISTNIEJACE-BO", failure.Message);

        // Sam rollback na połączeniu SQL nie wystarcza: DbContext po wyjątku
        // nie może nadal zwracać zamkniętego roku i nieistniejącego roku 2027.
        var reopened = await books.Db.FiscalYears.SingleAsync(f => f.Id == fiscalYearId);
        Assert.Equal(FiscalYearStatus.Otwarty, reopened.Status);
        Assert.False(await books.Db.FiscalYears.AnyAsync(f => f.StartDate.Year == 2027));
        Assert.False(await books.Db.JournalEntries.AnyAsync(e => e.Kind == EntryKind.Zamkniecie));
        Assert.False(await books.Db.JournalEntries.AnyAsync(e => e.Kind == EntryKind.BilansOtwarcia));

        // Również niezwiązany SaveChanges nie może utrwalić częściowego zamknięcia.
        await books.AddCounterpartyAsync("POSTFAIL", "Kolejny kontrahent");
        books.Db.ChangeTracker.Clear();
        Assert.Equal(FiscalYearStatus.Otwarty,
            (await books.Db.FiscalYears.AsNoTracking().SingleAsync(f => f.Id == fiscalYearId)).Status);

        // Po naprawieniu konfiguracji można wykonać poprawne zamknięcie,
        // bez duplikatów zapisów i bez konieczności restartu programu.
        posting.RetainedEarnings = "820";
        await settings.SetAsync(books.Company.Id, PostingSettings.SettingKey, posting);
        await books.Db.SaveChangesAsync();
        var next = await service.CloseYearAsync(fiscalYearId, "ZAMKNIJ 2026",
            "Wyniki uzgodnione, ponowna próba po poprawieniu ustawień");
        Assert.Equal(2027, next.StartDate.Year);
        Assert.Equal(1, await books.Db.JournalEntries.CountAsync(e => e.Kind == EntryKind.Zamkniecie));
        Assert.Equal(1, await books.Db.JournalEntries.CountAsync(e => e.Kind == EntryKind.BilansOtwarcia));
    }
}
