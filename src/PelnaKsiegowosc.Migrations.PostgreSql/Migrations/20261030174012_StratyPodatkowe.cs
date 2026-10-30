using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxLossId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodNumber = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OneTime = table.Column<bool>(type: "boolean", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxLossDeductions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxLosses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    YearLabel = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    YearEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    CapitalGains = table.Column<bool>(type: "boolean", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
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
