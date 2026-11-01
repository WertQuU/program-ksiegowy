using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Infrastructure.Configuration;

/// <summary>Jawne pierwsze przygotowanie bazy pustej lub nieistniejącej. Nigdy nie aktualizuje istniejących ksiąg.</summary>
public static class PostgresFirstSetup
{
    public static async Task PrepareAsync(DatabaseProfile profile, string administratorLogin,
        string administratorPassword, string operatorPassword, CancellationToken ct = default)
    {
        if (profile.Engine != DatabaseEngine.PostgreSql || string.IsNullOrWhiteSpace(profile.Database)
            || Encoding.UTF8.GetByteCount(profile.Database) > 63
            || profile.Database is "postgres" or "template0" or "template1")
            throw new DomainException("Podaj osobną nazwę nowej bazy ksiąg (maksymalnie 63 bajty UTF-8).");
        if (!PostgresLoginPolicy.IsValid(profile.Username) || string.IsNullOrWhiteSpace(administratorLogin)
            || administratorLogin == profile.Username)
            throw new DomainException("Podaj osobne konto administratora i ograniczone konto operatora PostgreSQL.");
        if (string.IsNullOrEmpty(administratorPassword) || string.IsNullOrEmpty(operatorPassword))
            throw new DomainException("Pierwsza konfiguracja wymaga hasła administratora i hasła operatora.");

        var adminCs = new NpgsqlConnectionStringBuilder(profile.BuildConnectionString(administratorPassword))
        {
            Username = administratorLogin, Database = "postgres", Pooling = false
        };
        await using var server = new NpgsqlConnection(adminCs.ConnectionString);
        await server.OpenAsync(ct);
        await using (var roleCheck = new NpgsqlCommand("""
            SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR NOT rolcanlogin
            FROM pg_roles WHERE rolname=@login
            """, server))
        {
            roleCheck.Parameters.AddWithValue("login", profile.Username!);
            if (await roleCheck.ExecuteScalarAsync(ct) is true)
                throw new DomainException("Wybrane konto operatora ma uprawnienia administracyjne albo nie może się logować. Wybierz osobny ograniczony login.");
        }
        var databaseExists = false;
        await using (var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=@name)", server))
        {
            exists.Parameters.AddWithValue("name", profile.Database);
            databaseExists = await exists.ExecuteScalarAsync(ct) is true;
        }
        if (!databaseExists)
        {
            // CREATE DATABASE must run outside a transaction. Ownership stays with
            // the administrative account, never with the everyday operator.
            await using var create = new NpgsqlCommand($"CREATE DATABASE {Quote(profile.Database)} TEMPLATE template0 ENCODING 'UTF8'", server);
            await create.ExecuteNonQueryAsync(ct);
        }
        adminCs.Database = profile.Database;
        await using var target = new NpgsqlConnection(adminCs.ConnectionString);
        await target.OpenAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", target))
        {
            gate.Parameters.AddWithValue("key", ExclusivePostgresSession.MaintenanceLockKey);
            if (await gate.ExecuteScalarAsync(ct) is not true)
                throw new DomainException("Inny klient korzysta z bazy lub trwa jej konfiguracja. Zakończ pozostałe sesje.");
        }
        await using (var tables = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname NOT LIKE 'pg\_%' ESCAPE '\' AND n.nspname<>'information_schema'
                AND c.relkind IN ('r','p','v','m','f') AND c.relname<>'__EFMigrationsHistory')
            """, target))
        {
            if (await tables.ExecuteScalarAsync(ct) is true)
                throw new DomainException("Baza zawiera już tabele. Wybierz „Połącz i pobierz zapisane dane”. Pierwsza konfiguracja nie zmienia istniejących ksiąg.");
        }
        await using (var owner = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_roles r JOIN pg_database d ON d.datname=current_database()
              WHERE r.rolname=@login AND pg_has_role(r.oid,d.datdba,'MEMBER'))
            """, target))
        {
            owner.Parameters.AddWithValue("login", profile.Username!);
            if (await owner.ExecuteScalarAsync(ct) is true)
                throw new DomainException("Konto operatora jest właścicielem bazy lub należy do roli właściciela. Użyj osobnego ograniczonego loginu.");
        }
        await using (var roleExists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@login)", server))
        {
            roleExists.Parameters.AddWithValue("login", profile.Username!);
            if (await roleExists.ExecuteScalarAsync(ct) is not true)
            {
                await using var createRole = new NpgsqlCommand($"CREATE ROLE {Quote(profile.Username!)} LOGIN PASSWORD {Literal(operatorPassword)} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS", server);
                await createRole.ExecuteNonQueryAsync(ct);
            }
        }
        await using (var connect = new NpgsqlCommand($"GRANT CONNECT ON DATABASE {Quote(profile.Database)} TO {Quote(profile.Username!)}", target))
            await connect.ExecuteNonQueryAsync(ct);
        // Verify an existing operator's password before creating any application tables.
        await using (var probe = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(profile.BuildConnectionString(operatorPassword)) { Pooling = false }.ConnectionString))
            await probe.OpenAsync(ct);
        await using (var db = DbContextFactory.Create(DatabaseEngine.PostgreSql, adminCs.ConnectionString))
            await db.Database.MigrateAsync(ct);

        var loginSql = Quote(profile.Username!);
        await using (var grants = new NpgsqlCommand($"""
            REVOKE CREATE ON DATABASE {Quote(profile.Database)} FROM PUBLIC;
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            GRANT CONNECT ON DATABASE {Quote(profile.Database)} TO {loginSql};
            GRANT USAGE ON SCHEMA public TO {loginSql};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {loginSql};
            REVOKE INSERT, UPDATE, DELETE ON TABLE public."__EFMigrationsHistory" FROM {loginSql};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {loginSql};
            """, target)) await grants.ExecuteNonQueryAsync(ct);
        // Release the maintenance lock before testing the ordinary operator session.
        await target.CloseAsync();
        await using var validated = await ExclusivePostgresSession.AcquireAsync(profile,
            profile.BuildConnectionString(operatorPassword), ct, requireOperatorRole: true);
    }

    private static string Quote(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    private static string Literal(string value) => "E'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
}
