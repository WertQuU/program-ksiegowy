using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
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
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeductibleUntilEvidenceId",
                table: "DebtFinancingCarryforwards",
                type: "uuid",
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
