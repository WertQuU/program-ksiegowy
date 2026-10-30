using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Magazyn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    InventoryAccount = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IssueAccount = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Value = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StockItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Value = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    QuantityAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    ValueAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CounterpartyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_CompanyId_Code",
                table: "StockItems",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId_StockItemId_Date",
                table: "StockMovements",
                columns: new[] { "CompanyId", "StockItemId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockItems");

            migrationBuilder.DropTable(
                name: "StockMovements");
        }
    }
}
