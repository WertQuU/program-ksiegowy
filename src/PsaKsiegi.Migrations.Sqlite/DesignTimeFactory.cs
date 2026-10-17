using Microsoft.EntityFrameworkCore.Design;
using PsaKsiegi.Data;

namespace PsaKsiegi.Migrations.Sqlite;

/// <summary>Fabryka używana wyłącznie przez narzędzie dotnet-ef podczas generowania migracji.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<KsiegiDbContext>
{
    public KsiegiDbContext CreateDbContext(string[] args) =>
        DbContextFactory.Create(DatabaseEngine.Sqlite, "Data Source=design-time.db");
}
