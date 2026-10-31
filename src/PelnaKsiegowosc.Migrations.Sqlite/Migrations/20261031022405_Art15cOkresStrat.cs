using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DebtFinancing",
                table: "Accounts",
                type: "TEXT",
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
