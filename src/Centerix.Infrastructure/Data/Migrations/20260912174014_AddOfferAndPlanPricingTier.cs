using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOfferAndPlanPricingTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Offers",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    PlanId = table.Column<int>(type: "int", nullable: false),
                    DurationMonths = table.Column<int>(type: "int", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FinalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MonthlyListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    PromotionId = table.Column<int>(type: "int", nullable: true),
                    PromotionName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PromotionCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PromotionType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DiscountPercentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    ChargedMonths = table.Column<int>(type: "int", nullable: true),
                    CalculatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConvertedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlanPricingTiers",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlanId = table.Column<int>(type: "int", nullable: false),
                    DurationMonths = table.Column<int>(type: "int", nullable: false),
                    TierPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    PlanId1 = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanPricingTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanPricingTiers_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "Platform",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanPricingTiers_Plans_PlanId1",
                        column: x => x.PlanId1,
                        principalSchema: "Platform",
                        principalTable: "Plans",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Offers_ExpiresAtUtc",
                schema: "Platform",
                table: "Offers",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_PlanId",
                schema: "Platform",
                table: "Offers",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_TenantId_CreatedAt",
                schema: "Platform",
                table: "Offers",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Offers_TenantId_Status",
                schema: "Platform",
                table: "Offers",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanPricingTiers_PlanId",
                schema: "Platform",
                table: "PlanPricingTiers",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanPricingTiers_PlanId1",
                schema: "Platform",
                table: "PlanPricingTiers",
                column: "PlanId1");

            migrationBuilder.CreateIndex(
                name: "UX_PlanPricingTiers_PlanId_DurationMonths",
                schema: "Platform",
                table: "PlanPricingTiers",
                columns: new[] { "PlanId", "DurationMonths" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Offers",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PlanPricingTiers",
                schema: "Platform");
        }
    }
}
