using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WhiteListCheckedOn",
                table: "Counterparties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhiteListRequestId",
                table: "Counterparties",
                type: "TEXT",
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
