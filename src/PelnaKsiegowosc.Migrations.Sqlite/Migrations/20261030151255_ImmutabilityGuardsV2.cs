using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ImmutabilityGuardsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in PelnaKsiegowosc.Data.ImmutabilityTriggers.SqliteV2) migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in PelnaKsiegowosc.Data.ImmutabilityTriggers.SqliteV2Drop) migrationBuilder.Sql(sql);
        }
    }
}
