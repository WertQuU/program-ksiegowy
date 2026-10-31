using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class KosztyAutorskieLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AuthorCosts",
                table: "PayrollItems",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BaseGross",
                table: "PayrollItems",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "AuthorCostsBasis",
                table: "Employees",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AuthorSharePercent",
                table: "Employees",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorCosts",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "BaseGross",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "AuthorCostsBasis",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "AuthorSharePercent",
                table: "Employees");
        }
    }
}
