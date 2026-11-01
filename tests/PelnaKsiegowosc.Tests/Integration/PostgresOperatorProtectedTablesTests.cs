using Npgsql;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Infrastructure.Configuration;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

/// <summary>
/// Operator nie może zmieniać audytu ani historii migracji nawet wtedy, gdy
/// niebezpieczne prawa pochodzą z innej roli PostgreSQL.
/// </summary>
public sealed class PostgresOperatorProtectedTablesTests
{
    [PostgresFact]
    public async Task Operator_session_rejects_inherited_audit_and_migration_mutation_rights()
    {
        var (ownerDb, adminConnectionString, databaseName) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var ownerCleanup = ownerDb;
        var login = "pk_guard_" + Guid.NewGuid().ToString("N")[..12];
        var inherited = "pk_guard_group_" + Guid.NewGuid().ToString("N")[..12];
        var password = Guid.NewGuid().ToString("N");

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var provision = new NpgsqlCommand($"""
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            CREATE TABLE public."AuditEvents" ("Id" integer PRIMARY KEY);
            CREATE TABLE public."__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY);
            CREATE ROLE "{login}" LOGIN PASSWORD '{password}' INHERIT;
            CREATE ROLE "{inherited}" NOLOGIN;
            GRANT CONNECT ON DATABASE "{databaseName}" TO "{login}";
            GRANT USAGE ON SCHEMA public TO "{login}";
            GRANT SELECT, INSERT ON TABLE public."AuditEvents" TO "{login}";
            GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO "{login}";
            GRANT "{inherited}" TO "{login}";
            """, admin))
            await provision.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = login,
            Password = password,
            Pooling = false
        };
        var profile = new DatabaseProfile
        {
            Engine = DatabaseEngine.PostgreSql,
            Host = builder.Host,
            Port = builder.Port,
            Database = databaseName,
            Username = login,
            SslMode = PgSslMode.Disable
        };

        try
        {
            // Dozwolone uprawnienia: dopisywanie audytu i odczyt historii migracji.
            await using (var allowed = await ExclusivePostgresSession.AcquireAsync(
                profile, builder.ConnectionString, requireOperatorRole: true))
                Assert.True(allowed.IsHealthy);

            // Prawo UPDATE pochodzi wyłącznie z roli dziedziczonej.
            await using (var grant = new NpgsqlCommand(
                $"""GRANT UPDATE ON public."AuditEvents" TO "{inherited}";""", admin))
                await grant.ExecuteNonQueryAsync();
            var auditError = await Assert.ThrowsAsync<DomainException>(() =>
                ExclusivePostgresSession.AcquireAsync(
                    profile, builder.ConnectionString, requireOperatorRole: true));
            Assert.Contains("AuditEvents", auditError.Message);

            await using (var revoke = new NpgsqlCommand(
                $"""REVOKE UPDATE ON public."AuditEvents" FROM "{inherited}";""", admin))
                await revoke.ExecuteNonQueryAsync();

            // GRANT dla PUBLIC też jest efektywnym uprawnieniem każdego operatora.
            await using (var grant = new NpgsqlCommand(
                """GRANT DELETE ON public."AuditEvents" TO PUBLIC;""", admin))
                await grant.ExecuteNonQueryAsync();
            var publicAuditError = await Assert.ThrowsAsync<DomainException>(() =>
                ExclusivePostgresSession.AcquireAsync(
                    profile, builder.ConnectionString, requireOperatorRole: true));
            Assert.Contains("AuditEvents", publicAuditError.Message);
            await using (var revoke = new NpgsqlCommand(
                """REVOKE DELETE ON public."AuditEvents" FROM PUBLIC;""", admin))
                await revoke.ExecuteNonQueryAsync();

            // Prawo INSERT do historii migracji jest równie niedozwolone.
            await using (var grant = new NpgsqlCommand(
                $"""GRANT INSERT ON public."__EFMigrationsHistory" TO "{inherited}";""", admin))
                await grant.ExecuteNonQueryAsync();
            var migrationError = await Assert.ThrowsAsync<DomainException>(() =>
                ExclusivePostgresSession.AcquireAsync(
                    profile, builder.ConnectionString, requireOperatorRole: true));
            Assert.Contains("__EFMigrationsHistory", migrationError.Message);

            await using (var revoke = new NpgsqlCommand(
                $"""REVOKE INSERT ON public."__EFMigrationsHistory" FROM "{inherited}";""", admin))
                await revoke.ExecuteNonQueryAsync();

            // Wykrywane są również uprawnienia nadane bezpośrednio operatorowi.
            await using (var grant = new NpgsqlCommand(
                $"""GRANT DELETE ON public."__EFMigrationsHistory" TO "{login}";""", admin))
                await grant.ExecuteNonQueryAsync();
            var directMigrationError = await Assert.ThrowsAsync<DomainException>(() =>
                ExclusivePostgresSession.AcquireAsync(
                    profile, builder.ConnectionString, requireOperatorRole: true));
            Assert.Contains("__EFMigrationsHistory", directMigrationError.Message);
            await using (var revoke = new NpgsqlCommand(
                $"""REVOKE DELETE ON public."__EFMigrationsHistory" FROM "{login}";""", admin))
                await revoke.ExecuteNonQueryAsync();

            // Odmowa dostępu zwalnia połączenie i blokadę; po usunięciu nadmiarowych
            // praw operator może ponownie otworzyć księgi bez restartu PostgreSQL.
            await using (var allowedAgain = await ExclusivePostgresSession.AcquireAsync(
                profile, builder.ConnectionString, requireOperatorRole: true))
                Assert.True(allowedAgain.IsHealthy);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"""
                REVOKE "{inherited}" FROM "{login}";
                REVOKE ALL PRIVILEGES ON TABLE public."AuditEvents", public."__EFMigrationsHistory"
                    FROM "{login}", "{inherited}";
                REVOKE ALL PRIVILEGES ON SCHEMA public FROM "{login}", "{inherited}";
                REVOKE ALL PRIVILEGES ON DATABASE "{databaseName}" FROM "{login}", "{inherited}";
                DROP ROLE "{login}";
                DROP ROLE "{inherited}";
                """, admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
