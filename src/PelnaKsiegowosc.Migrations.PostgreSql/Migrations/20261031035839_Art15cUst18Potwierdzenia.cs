using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PelnaKsiegowosc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Art15cUst18Potwierdzenia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeductibleUntilBasis",
                table: "TaxLosses",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExceptionsStatement",
                table: "DebtFinancingCarryforwardUses",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DeductibleUntilBasis",
                table: "DebtFinancingCarryforwards",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeductibleUntilBasis",
                table: "TaxLosses");

            migrationBuilder.DropColumn(
                name: "ExceptionsStatement",
                table: "DebtFinancingCarryforwardUses");

            migrationBuilder.DropColumn(
                name: "DeductibleUntilBasis",
                table: "DebtFinancingCarryforwards");
        }
    }
}
