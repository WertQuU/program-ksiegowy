using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PlacePit2ZbiegPpk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VoluntaryPension",
                table: "Employees",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PpkTransferredOn",
                table: "PayrollLists",
                type: "date",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "BaseGross",
                table: "PayrollItems",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AddColumn<bool>(
                name: "InsuranceUndetermined",
                table: "PayrollItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PpkEmployerTaxed",
                table: "PayrollItems",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "PpkTaxedInListId",
                table: "PayrollItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxReduction",
                table: "PayrollItems",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTitles",
                table: "Employees",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Nieustalone");

            migrationBuilder.AddColumn<decimal>(
                name: "ExternalTitlesBasis",
                table: "Employees",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Pit2Reduction",
                table: "Employees",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // PIT-2: dotychczasowe oświadczenie (tak/nie) — kwota 1/12 = 300 zł
            migrationBuilder.Sql("UPDATE \"Employees\" SET \"Pit2Reduction\" = 300 WHERE \"Pit2\" = TRUE");
            migrationBuilder.DropColumn(name: "Pit2", table: "Employees");
            // wynagrodzenie bazowe 0 oznaczało dotąd brak kwoty (przyjmowano kwotę z ewidencji osoby) — teraz null
            migrationBuilder.Sql("UPDATE \"PayrollItems\" SET \"BaseGross\" = NULL WHERE \"BaseGross\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Pit2",
                table: "Employees",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            migrationBuilder.Sql("UPDATE \"Employees\" SET \"Pit2\" = TRUE WHERE \"Pit2Reduction\" > 0");
            migrationBuilder.Sql("UPDATE \"PayrollItems\" SET \"BaseGross\" = 0 WHERE \"BaseGross\" IS NULL");

            migrationBuilder.DropColumn(
                name: "PpkTransferredOn",
                table: "PayrollLists");

            migrationBuilder.DropColumn(
                name: "InsuranceUndetermined",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "PpkEmployerTaxed",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "PpkTaxedInListId",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "TaxReduction",
                table: "PayrollItems");

            migrationBuilder.DropColumn(
                name: "ExternalTitles",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "ExternalTitlesBasis",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Pit2Reduction",
                table: "Employees");

            migrationBuilder.DropColumn(name: "VoluntaryPension", table: "Employees");

            migrationBuilder.AlterColumn<decimal>(
                name: "BaseGross",
                table: "PayrollItems",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldNullable: true);
        }
    }
}
