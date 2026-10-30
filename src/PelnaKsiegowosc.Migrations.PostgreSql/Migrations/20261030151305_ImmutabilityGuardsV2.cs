using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ImmutabilityGuardsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in PelnaKsiegowosc.Data.ImmutabilityTriggers.PostgreSqlV2) migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in PelnaKsiegowosc.Data.ImmutabilityTriggers.PostgreSqlV2Drop) migrationBuilder.Sql(sql);
        }
    }
}
