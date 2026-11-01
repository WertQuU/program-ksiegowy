using Npgsql;
using PelnaKsiegowosc.Application.Abstractions;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Presentation.Services;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class PostgresSessionReleaseTests
{
    [PostgresFact]
    public async Task Failed_open_due_to_pending_migrations_releases_exclusive_operator_session()
    {
        var (ownerDb, cs, databaseName) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var ownerCleanup = ownerDb;
        var role = "pk_retry_" + Guid.NewGuid().ToString("N")[..12];
        var password = Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(cs);
        await admin.OpenAsync();

        await using (var provision = new NpgsqlCommand($"""
            CREATE ROLE "{role}" LOGIN PASSWORD '{password}';
            GRANT CONNECT ON DATABASE "{databaseName}" TO "{role}";
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO "{role}";
            """, admin))
            await provision.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(cs);
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql,
            Host = builder.Host,
            Port = builder.Port,
            Database = databaseName,
            Username = role,
            SslMode = PgSslMode.Disable
        };
        var secrets = new InMemorySecretStore();
        secrets.Set(profile.PasswordSecretKey, password);
        var directory = Path.Combine(Path.GetTempPath(), "pk-open-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var session = new AppSession(new AppPaths(directory),
            new AppConfig { ActiveDatabase = profile }, secrets, new SystemClock());

        try
        {
            // The operator can connect, but the database has no migrations.
            // Opening must fail WITHOUT retaining its advisory session lock.
            var result = await session.OpenAsync();
            Assert.False(result.Success);
            Assert.Contains("nieaktualny schemat", result.Error);

            // Before the fix this throws because OpenAsync still owns the lock.
            await using var retry = await ExclusivePostgresSession.AcquireAsync(
                profile, profile.BuildConnectionString(password), requireOperatorRole: true);
            Assert.True(retry.IsHealthy);
        }
        finally
        {
            session.ReleaseOperatorSession();
            await using (var revoke = new NpgsqlCommand($"""
                REVOKE ALL PRIVILEGES ON SCHEMA public FROM "{role}";
                REVOKE ALL PRIVILEGES ON DATABASE "{databaseName}" FROM "{role}";
                DROP ROLE "{role}";
                """, admin))
                await revoke.ExecuteNonQueryAsync();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
