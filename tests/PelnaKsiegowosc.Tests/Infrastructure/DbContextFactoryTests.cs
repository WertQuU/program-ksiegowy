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

    [Fact]
    public void Repeated_encrypted_sqlite_contexts_reuse_internal_services()
    {
        var key = SqliteEncryption.NewKey();
        var providers = new List<IDatabaseProvider>();

        for (var i = 0; i < 32; i++)
        {
            var profile = new DatabaseProfile
            {
                Engine = DatabaseEngine.Sqlite,
                SqlitePath = Path.Combine(Path.GetTempPath(), $"pk-sqlite-options-{Guid.NewGuid():N}.db")
            };
            using var db = DbContextFactory.Create(
                DatabaseEngine.Sqlite, profile.BuildConnectionString(null, encrypted: true), key);
            providers.Add(db.GetService<IDatabaseProvider>());
            Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
        }

        Assert.All(providers, provider => Assert.Same(providers[0], provider));
    }

    [Fact]
    public async Task Encrypted_sqlite_contexts_do_not_share_their_keys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pk-sqlite-key-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.Sqlite,
            SqlitePath = Path.Combine(directory, "ksiegi.db")
        };
        var connectionString = profile.BuildConnectionString(null, encrypted: true);
        var correctKey = SqliteEncryption.NewKey();
        var wrongKey = SqliteEncryption.NewKey();

        try
        {
            await using (var db = DbContextFactory.Create(DatabaseEngine.Sqlite, connectionString, correctKey))
            {
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE KeyProbe (Value INTEGER NOT NULL)");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO KeyProbe (Value) VALUES (123)");
            }

            // The file must actually be SQLCipher-encrypted before testing key isolation.
            Assert.True(SqliteEncryption.IsEncrypted(profile.SqlitePath!));

            // A wrong key must be rejected while opening, not only on a later query.
            await using (var db = DbContextFactory.Create(DatabaseEngine.Sqlite, connectionString, wrongKey))
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.OpenConnectionAsync());

            await using (var db = DbContextFactory.Create(DatabaseEngine.Sqlite, connectionString, correctKey))
            {
                await db.Database.OpenConnectionAsync();
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT Value FROM KeyProbe";
                Assert.Equal(123L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
