using Microsoft.EntityFrameworkCore;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class PostgresProtectedFieldsTests
{
    [PostgresFact]
    public async Task Pesel_is_encrypted_in_postgres_table_but_clear_in_the_program()
    {
        var (db, _, _) = await PostgresTests.CreateEmptyPostgresAsync();
        await using var cleanup = db;
        await db.Database.MigrateAsync();
        var firm = new Company
        {
            FullName = "Spółka Testowa",
            ShortName = "Test",
            Nip = "5260250274",
            Street = "Testowa",
            BuildingNumber = "1",
            PostalCode = "00-001",
            City = "Warszawa"
        };
        db.Companies.Add(firm);
        var person = new ResponsiblePerson
        {
            CompanyId = firm.Id,
            FirstName = "Anna",
            LastName = "Przykładowa",
            Pesel = "44051401359"
        };
        db.ResponsiblePersons.Add(person);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var fromProgram = await db.ResponsiblePersons.AsNoTracking().SingleAsync(p => p.Id == person.Id);
        Assert.Equal("44051401359", fromProgram.Pesel);
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var sql = db.Database.GetDbConnection().CreateCommand();
            sql.CommandText = "SELECT \"Pesel\" FROM \"ResponsiblePersons\" LIMIT 1";
            var onServer = (string?)await sql.ExecuteScalarAsync();
            Assert.NotNull(onServer);
            Assert.StartsWith(CompanyFieldCodec.Prefix, onServer);
            Assert.DoesNotContain("44051401359", onServer);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
