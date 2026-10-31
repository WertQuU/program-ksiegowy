using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class MultiCompanyAccessSafetyTests
{
    [Fact]
    public async Task Session_refuses_database_with_two_companies_until_per_company_permissions_exist()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-tenants-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (session, _) = SelfTestScenario.CreateSession(root);
            Directory.CreateDirectory(Path.GetDirectoryName(session.Profile.SqlitePath!)!);
            await using (var db = DbContextFactory.Create(DatabaseEngine.Sqlite,
                             session.Profile.BuildConnectionString(null)))
            {
                await db.Database.MigrateAsync();
                db.Companies.AddRange(
                    new Company { Id = Guid.CreateVersion7(), FullName = "Jednostka 1", Nip = "5260250274" },
                    new Company { Id = Guid.CreateVersion7(), FullName = "Jednostka 2", Nip = "7740001454" });
                await db.SaveChangesAsync();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var ex = await Assert.ThrowsAsync<DomainException>(() => session.RefreshAsync());
            Assert.Contains("kilka jednostek", ex.Message);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
