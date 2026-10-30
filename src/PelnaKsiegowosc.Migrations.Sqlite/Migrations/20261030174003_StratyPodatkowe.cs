using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class StratyPodatkowe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaxLossDeductions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaxLossId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    OneTime = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxLossDeductions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxLosses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    YearLabel = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    YearEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CapitalGains = table.Column<bool>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxLosses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxLossDeductions_TaxLossId",
                table: "TaxLossDeductions",
                column: "TaxLossId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxLossDeductions_TaxLossId_FiscalYearId",
                table: "TaxLossDeductions",
                columns: new[] { "TaxLossId", "FiscalYearId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxLosses_CompanyId",
                table: "TaxLosses",
                column: "CompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxLossDeductions");

            migrationBuilder.DropTable(
                name: "TaxLosses");
        }
    }
}
