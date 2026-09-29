using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEligibilityRuleToBenefits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EligibilityRule",
                schema: "Platform",
                table: "OfferBenefits",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EligibilityRule",
                schema: "Platform",
                table: "ContractBenefits",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EligibilityRule",
                schema: "Platform",
                table: "OfferBenefits");

            migrationBuilder.DropColumn(
                name: "EligibilityRule",
                schema: "Platform",
                table: "ContractBenefits");
        }
    }
}
