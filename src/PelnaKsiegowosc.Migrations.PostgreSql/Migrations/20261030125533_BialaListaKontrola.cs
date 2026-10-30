using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class BialaListaKontrola : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WhiteListAccountOk",
                table: "Counterparties",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WhiteListCheckedOn",
                table: "Counterparties",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhiteListRequestId",
                table: "Counterparties",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhiteListAccountOk",
                table: "Counterparties");

            migrationBuilder.DropColumn(
                name: "WhiteListCheckedOn",
                table: "Counterparties");

            migrationBuilder.DropColumn(
                name: "WhiteListRequestId",
                table: "Counterparties");
        }
    }
}
