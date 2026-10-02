using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropContractBenefitIsGrantedColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContractBenefits_ContractId_IsGranted",
                schema: "Platform",
                table: "ContractBenefits");

            migrationBuilder.DropColumn(
                name: "IsGranted",
                schema: "Platform",
                table: "ContractBenefits");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsGranted",
                schema: "Platform",
                table: "ContractBenefits",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ContractBenefits_ContractId_IsGranted",
                schema: "Platform",
                table: "ContractBenefits",
                columns: new[] { "ContractId", "IsGranted" });
        }
    }
}
