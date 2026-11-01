using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using PelnaKsiegowosc.Data;

namespace PelnaKsiegowosc.Tests.Infrastructure;

public sealed class DbContextFactoryTests
{
    [Fact]
    public void Repeated_postgres_configuration_reuses_services_without_opening_connection()
    {
        var providers = new List<IDatabaseProvider>();
        for (var i = 0; i < 32; i++)
        {
            // Changing profiles must not create a new internal service provider.
            var options = new DbContextOptionsBuilder<KsiegiDbContext>(DbContextFactory.BuildOptions(
                DatabaseEngine.PostgreSql,
                $"Host=localhost;Database=configuration_{i};Username=operator;Password=test"))
                .ConfigureWarnings(w => w.Throw(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            using var db = new KsiegiDbContext(options);
            var current = db.GetService<IDatabaseProvider>();
            providers.Add(current);
            Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        Assert.All(providers, provider => Assert.Same(providers[0], provider));
    }
}
