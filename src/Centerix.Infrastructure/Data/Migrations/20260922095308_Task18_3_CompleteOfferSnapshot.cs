using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task18_3_CompleteOfferSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BonusMonths",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EntitlementSnapshotVersion",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxBranches",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxStudents",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxTeachers",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxUsers",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SMSQuota",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StorageGB",
                schema: "Platform",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "OfferFeatures",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeatureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfferFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfferFeatures_Offers_OfferId",
                        column: x => x.OfferId,
                        principalSchema: "Platform",
                        principalTable: "Offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OfferPricingTiers",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DurationMonths = table.Column<int>(type: "int", nullable: false),
                    TierPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfferPricingTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfferPricingTiers_Offers_OfferId",
                        column: x => x.OfferId,
                        principalSchema: "Platform",
                        principalTable: "Offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfferFeatures_OfferId",
                schema: "Platform",
                table: "OfferFeatures",
                column: "OfferId");

            migrationBuilder.CreateIndex(
                name: "IX_OfferFeatures_OfferId_FeatureCode",
                schema: "Platform",
                table: "OfferFeatures",
                columns: new[] { "OfferId", "FeatureCode" });

            migrationBuilder.CreateIndex(
                name: "IX_OfferPricingTiers_OfferId",
                schema: "Platform",
                table: "OfferPricingTiers",
                column: "OfferId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfferFeatures",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "OfferPricingTiers",
                schema: "Platform");

            migrationBuilder.DropColumn(
                name: "BonusMonths",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "EntitlementSnapshotVersion",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "MaxBranches",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "MaxStudents",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "MaxTeachers",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "MaxUsers",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "SMSQuota",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "StorageGB",
                schema: "Platform",
                table: "Offers");
        }
    }
}
