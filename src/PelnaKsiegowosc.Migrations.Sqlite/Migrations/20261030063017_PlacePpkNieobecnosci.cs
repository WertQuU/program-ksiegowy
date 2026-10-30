using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PlacePpkNieobecnosci : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PpkEmployee",
                table: "PayrollItems",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PpkEmployer",
                table: "PayrollItems",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SickDays",
                table: "PayrollItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SickPay",
                table: "PayrollItems",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "UnpaidDays",
                table: "PayrollItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Ppk",
                table: "Employees",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PpkEmployeeRate",
                table: "Employees",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PpkEmployerRate",
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
                name: "PpkEmployee",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "PpkEmployer",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "SickDays",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "SickPay",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "UnpaidDays",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "Ppk",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PpkEmployeeRate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PpkEmployerRate",
                table: "Employees");
        }
    }
}
