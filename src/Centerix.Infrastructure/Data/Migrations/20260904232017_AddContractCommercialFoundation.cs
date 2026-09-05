using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractCommercialFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                schema: "Platform",
                table: "TenantPlans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Contracts",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    PlanId = table.Column<int>(type: "int", nullable: false),
                    EffectiveAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMonths = table.Column<int>(type: "int", nullable: false),
                    MonthlyListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ContractedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PromotionReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContractBenefits",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BenefitType = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContractualValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    IsGranted = table.Column<bool>(type: "bit", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractBenefits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractBenefits_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "Platform",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractPricingTiers",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DurationMonths = table.Column<int>(type: "int", nullable: false),
                    TierPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    MonthlyListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractPricingTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractPricingTiers_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "Platform",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlans_ContractId",
                schema: "Platform",
                table: "TenantPlans",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractBenefits_ContractId",
                schema: "Platform",
                table: "ContractBenefits",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractBenefits_ContractId_IsGranted",
                schema: "Platform",
                table: "ContractBenefits",
                columns: new[] { "ContractId", "IsGranted" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractPricingTiers_ContractId",
                schema: "Platform",
                table: "ContractPricingTiers",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "UX_ContractPricingTiers_ContractId_DurationMonths",
                schema: "Platform",
                table: "ContractPricingTiers",
                columns: new[] { "ContractId", "DurationMonths" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_EffectiveAtUtc",
                schema: "Platform",
                table: "Contracts",
                column: "EffectiveAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_EndsAtUtc",
                schema: "Platform",
                table: "Contracts",
                column: "EndsAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_PlanId",
                schema: "Platform",
                table: "Contracts",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_Status",
                schema: "Platform",
                table: "Contracts",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Contracts_TenantId_ContractNumber",
                schema: "Platform",
                table: "Contracts",
                columns: new[] { "TenantId", "ContractNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TenantPlans_Contracts_ContractId",
                schema: "Platform",
                table: "TenantPlans",
                column: "ContractId",
                principalSchema: "Platform",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TenantPlans_Contracts_ContractId",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropTable(
                name: "ContractBenefits",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "ContractPricingTiers",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Contracts",
                schema: "Platform");

            migrationBuilder.DropIndex(
                name: "IX_TenantPlans_ContractId",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "ContractId",
                schema: "Platform",
                table: "TenantPlans");
        }
    }
}
