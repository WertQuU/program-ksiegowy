using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Application.Ledger;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Accounts;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Counterparties;
using PelnaKsiegowosc.Domain.Periods;
using PelnaKsiegowosc.Domain.Security;

namespace PelnaKsiegowosc.Tests.Infrastructure;

/// <summary>
/// Środowisko testowe: plik SQLite z pełnymi migracjami (w tym wyzwalaczami), skonfigurowana P.S.A.,
/// rok 2026, zatwierdzony plan kont z szablonu i zatwierdzona polityka rachunkowości.
/// </summary>
public sealed class TestBooks : IAsyncDisposable
{
    public const string ValidNip = "5260250274"; // NIP o poprawnej sumie kontrolnej (przykładowy, z publicznych przykładów walidacji)

    public string Directory { get; }
    public string DbPath { get; }
    public KsiegiDbContext Db { get; private set; }
    public FixedClock Clock { get; }
    public CurrentUser User { get; private set; } = null!;
    public Company Company { get; private set; } = null!;
    public FiscalYear Year { get; private set; } = null!;
    public ChartOfAccounts Chart { get; private set; } = null!;

    private TestBooks(string dir)
    {
        Directory = dir;
        DbPath = Path.Combine(dir, "ksiegi.db");
        Clock = new FixedClock(new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
        Db = Open(DbPath);
    }

    public static KsiegiDbContext Open(string path) =>
        DbContextFactory.Create(DatabaseEngine.Sqlite, new DatabaseProfile { Engine = DatabaseEngine.Sqlite, SqlitePath = path }.BuildConnectionString(null));

    public static async Task<TestBooks> CreateAsync(bool approvePolicy = true, bool approveChart = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "psa-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var t = new TestBooks(dir);
        await t.Db.Database.MigrateAsync();
        await t.SeedAsync(approvePolicy, approveChart);
        return t;
    }

    private async Task SeedAsync(bool approvePolicy, bool approveChart)
    {
        var setup = new SetupService(Db, Clock);
        var sys = CurrentUser.System(Guid.Empty);
        Company = await setup.CreateCompanyAsync(sys, new Company
        {
            FullName = "Testowa Prosta Spółka Akcyjna",
            LegalForm = LegalForm.ProstaSpolkaAkcyjna,
            ShortName = "Testowa P.S.A.",
            Nip = ValidNip,
            Krs = "0000123456",
            Street = "ul. Przykładowa",
            BuildingNumber = "1",
            PostalCode = "00-001",
            City = "Warszawa",
            TaxOfficeCode = "1471"
        });
        User = new CurrentUser(Guid.NewGuid(), "ksiegowy", Company.Id, new[] { Role.Administrator, Role.GlownyKsiegowy, Role.Zarzad });
        Year = await setup.CreateFiscalYearAsync(User, Company.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), true);
        Chart = await new ChartService(Db, User, Clock).CreateFromTemplateAsync(Year.Id);
        if (approveChart) await setup.ApproveChartAsync(User, Chart.Id);
        var policy = await setup.CreatePolicyFromTemplateAsync(User, Company.Id);
        if (approvePolicy)
        {
            var content = policy.Content.Replace("[DO UZUPEŁNIENIA", "[uzupełniono").Replace("[uzupełniono]", "uzupełniono");
            await setup.SavePolicyAsync(User, policy.Id, content, CorrectionMethod.StornoCzerwone, 10000m, true, false);
            await setup.ApprovePolicyAsync(User, policy.Id, "Jan Testowy — prezes zarządu");
        }
    }

    public LedgerService Ledger() => new(Db, User, Clock);

    /// <summary>Symuluje ponowne uruchomienie aplikacji — nowy kontekst na tym samym pliku.</summary>
    public async Task ReopenAsync()
    {
        await Db.DisposeAsync();
        Db = Open(DbPath);
        Company = await Db.Companies.FirstAsync();
        Year = await Db.FiscalYears.Include(f => f.Periods).OrderBy(f => f.StartDate).FirstAsync();
    }

    public async Task<Counterparty> AddCounterpartyAsync(string code, string name, string? nip = null)
    {
        var c = new Counterparty { CompanyId = Company.Id, Code = code, Name = name, Nip = nip, City = "Kraków", PostalCode = "30-001", Street = "Rynek", BuildingNumber = "1", CreatedAtUtc = Clock.UtcNow };
        Db.Counterparties.Add(c);
        await Db.SaveChangesAsync();
        return c;
    }

    public static EntryInput Entry(DateOnly date, string doc, params LineInput[] lines) =>
        new("PK", date, date, date, $"Operacja {doc}", "PK", doc, lines);

    public async Task<decimal> BalanceAsync(string accountNumber, DateOnly? asOf = null)
    {
        var tb = await new LedgerQueries(Db).TrialBalanceAsync(Year.Id, Year.StartDate, asOf ?? Year.EndDate);
        return tb.First(t => t.Account.Number == accountNumber).Balance;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
