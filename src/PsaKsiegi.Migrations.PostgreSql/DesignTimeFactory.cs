using Microsoft.EntityFrameworkCore.Design;
using PsaKsiegi.Data;

namespace PsaKsiegi.Migrations.PostgreSql;

/// <summary>Fabryka używana wyłącznie przez narzędzie dotnet-ef podczas generowania migracji (połączenie nie jest nawiązywane).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<KsiegiDbContext>
{
    public KsiegiDbContext CreateDbContext(string[] args) =>
        DbContextFactory.Create(DatabaseEngine.PostgreSql, "Host=localhost;Database=design_time;Username=design_time");
}
