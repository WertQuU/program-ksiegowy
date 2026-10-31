using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Art15cRejestrNadwyzek : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DebtFinancingCarryforwards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    YearLabel = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    YearEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CapitalGains = table.Column<bool>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DeductibleUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebtFinancingCarryforwards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DebtFinancingCarryforwardUses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CarryforwardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebtFinancingCarryforwardUses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DebtFinancingCarryforwards_CompanyId",
                table: "DebtFinancingCarryforwards",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DebtFinancingCarryforwards_CompanyId_YearEnd_CapitalGains",
                table: "DebtFinancingCarryforwards",
                columns: new[] { "CompanyId", "YearEnd", "CapitalGains" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DebtFinancingCarryforwardUses_CarryforwardId",
                table: "DebtFinancingCarryforwardUses",
                column: "CarryforwardId");

            migrationBuilder.CreateIndex(
                name: "IX_DebtFinancingCarryforwardUses_CarryforwardId_FiscalYearId",
                table: "DebtFinancingCarryforwardUses",
                columns: new[] { "CarryforwardId", "FiscalYearId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DebtFinancingCarryforwards");

            migrationBuilder.DropTable(
                name: "DebtFinancingCarryforwardUses");
        }
    }
}
