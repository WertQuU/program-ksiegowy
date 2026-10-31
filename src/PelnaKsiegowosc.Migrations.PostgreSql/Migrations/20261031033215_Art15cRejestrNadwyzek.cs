using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    YearLabel = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    YearEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    CapitalGains = table.Column<bool>(type: "boolean", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DeductibleUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebtFinancingCarryforwards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DebtFinancingCarryforwardUses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarryforwardId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
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
