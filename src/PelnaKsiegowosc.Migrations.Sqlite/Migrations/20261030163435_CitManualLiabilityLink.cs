using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CitManualLiabilityLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CapitalGains",
                table: "CitAdjustments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenItemId",
                table: "CitAdjustments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CitAdjustments_OpenItemId",
                table: "CitAdjustments",
                column: "OpenItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_CitAdjustments_OpenItems_OpenItemId",
                table: "CitAdjustments",
                column: "OpenItemId",
                principalTable: "OpenItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CitAdjustments_OpenItems_OpenItemId",
                table: "CitAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_CitAdjustments_OpenItemId",
                table: "CitAdjustments");

            migrationBuilder.DropColumn(
                name: "CapitalGains",
                table: "CitAdjustments");

            migrationBuilder.DropColumn(
                name: "OpenItemId",
                table: "CitAdjustments");
        }
    }
}
