using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PsaKsiegi.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ImmutabilityGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in PsaKsiegi.Data.ImmutabilityTriggers.Sqlite) migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in PsaKsiegi.Data.ImmutabilityTriggers.SqliteDrop) migrationBuilder.Sql(sql);
        }
    }
}
