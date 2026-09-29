using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFreeMonthsBenefits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FreeMonthsBenefits",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementMonths = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    EligibilityStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    EligibleAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FulfillmentStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EligibilityRule = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FreeMonthsBenefits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FreeMonthsBenefits_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "Platform",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OfferFreeMonthsBenefits",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementMonths = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    EligibilityRule = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfferFreeMonthsBenefits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfferFreeMonthsBenefits_Offers_OfferId",
                        column: x => x.OfferId,
                        principalSchema: "Platform",
                        principalTable: "Offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FreeMonthsBenefits_ContractId",
                schema: "Platform",
                table: "FreeMonthsBenefits",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_FreeMonthsBenefits_ContractId_EligibilityStatus",
                schema: "Platform",
                table: "FreeMonthsBenefits",
                columns: new[] { "ContractId", "EligibilityStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_FreeMonthsBenefits_ContractId_FulfillmentStatus",
                schema: "Platform",
                table: "FreeMonthsBenefits",
                columns: new[] { "ContractId", "FulfillmentStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_OfferFreeMonthsBenefits_OfferId",
                schema: "Platform",
                table: "OfferFreeMonthsBenefits",
                column: "OfferId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FreeMonthsBenefits",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "OfferFreeMonthsBenefits",
                schema: "Platform");
        }
    }
}
