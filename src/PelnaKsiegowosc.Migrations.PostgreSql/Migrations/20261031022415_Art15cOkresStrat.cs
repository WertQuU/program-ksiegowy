using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Art15cOkresStrat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DeductibleUntil",
                table: "TaxLosses",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DebtFinancing",
                table: "Accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeductibleUntil",
                table: "TaxLosses");

            migrationBuilder.DropColumn(
                name: "DebtFinancing",
                table: "Accounts");
        }
    }
}
