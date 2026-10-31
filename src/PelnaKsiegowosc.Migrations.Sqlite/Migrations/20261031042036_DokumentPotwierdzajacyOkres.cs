using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DokumentPotwierdzajacyOkres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeductibleUntilEvidenceId",
                table: "TaxLosses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeductibleUntilEvidenceId",
                table: "DebtFinancingCarryforwards",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeductibleUntilEvidenceId",
                table: "TaxLosses");

            migrationBuilder.DropColumn(
                name: "DeductibleUntilEvidenceId",
                table: "DebtFinancingCarryforwards");
        }
    }
}
