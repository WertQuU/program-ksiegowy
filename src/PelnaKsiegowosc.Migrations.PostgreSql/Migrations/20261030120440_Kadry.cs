using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Kadry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnnualLeaveDays",
                table: "Employees",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BhpTrainingUntil",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LeaveCarriedOver",
                table: "Employees",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "MedicalExamUntil",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Absences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Absences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Absences_CompanyId_EmployeeId_From",
                table: "Absences",
                columns: new[] { "CompanyId", "EmployeeId", "From" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Absences");

            migrationBuilder.DropColumn(
                name: "AnnualLeaveDays",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "BhpTrainingUntil",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "LeaveCarriedOver",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "MedicalExamUntil",
                table: "Employees");
        }
    }
}
