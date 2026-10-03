using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionBenefitConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BenefitCurrencyCode",
                schema: "Platform",
                table: "Promotions",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BenefitDescription",
                schema: "Platform",
                table: "Promotions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BenefitName",
                schema: "Platform",
                table: "Promotions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "BenefitType",
                schema: "Platform",
                table: "Promotions",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BenefitValue",
                schema: "Platform",
                table: "Promotions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FreeMonthsCount",
                schema: "Platform",
                table: "Promotions",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BenefitCurrencyCode",
                schema: "Platform",
                table: "Promotions");

            migrationBuilder.DropColumn(
                name: "BenefitDescription",
                schema: "Platform",
                table: "Promotions");

            migrationBuilder.DropColumn(
                name: "BenefitName",
                schema: "Platform",
                table: "Promotions");

            migrationBuilder.DropColumn(
                name: "BenefitType",
                schema: "Platform",
                table: "Promotions");

            migrationBuilder.DropColumn(
                name: "BenefitValue",
                schema: "Platform",
                table: "Promotions");

            migrationBuilder.DropColumn(
                name: "FreeMonthsCount",
                schema: "Platform",
                table: "Promotions");
        }
    }
}
