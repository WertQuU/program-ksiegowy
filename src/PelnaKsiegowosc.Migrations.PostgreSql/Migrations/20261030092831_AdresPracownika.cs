using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AdresPracownika : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Commune",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "County",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FlatNumber",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HouseNumber",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Street",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Voivodeship",
                table: "Employees",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "City",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Commune",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "County",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "FlatNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "HouseNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Street",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Voivodeship",
                table: "Employees");
        }
    }
}
