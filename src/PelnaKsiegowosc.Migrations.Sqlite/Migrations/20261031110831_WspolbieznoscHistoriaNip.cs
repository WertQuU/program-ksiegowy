using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class WspolbieznoscHistoriaNip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "IdentityValidFrom",
                table: "Counterparties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CounterpartyIdentityChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CounterpartyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Nip = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CounterpartyIdentityChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CounterpartyIdentityChanges_CounterpartyId_ValidUntil",
                table: "CounterpartyIdentityChanges",
                columns: new[] { "CounterpartyId", "ValidUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CounterpartyIdentityChanges");

            migrationBuilder.DropColumn(
                name: "IdentityValidFrom",
                table: "Counterparties");
        }
    }
}
