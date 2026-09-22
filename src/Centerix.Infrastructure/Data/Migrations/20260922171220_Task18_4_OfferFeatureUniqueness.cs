using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task18_4_OfferFeatureUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OfferFeatures_OfferId_FeatureCode",
                schema: "Platform",
                table: "OfferFeatures");

            migrationBuilder.CreateIndex(
                name: "UX_OfferFeatures_OfferId_FeatureCode",
                schema: "Platform",
                table: "OfferFeatures",
                columns: new[] { "OfferId", "FeatureCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_OfferFeatures_OfferId_FeatureCode",
                schema: "Platform",
                table: "OfferFeatures");

            migrationBuilder.CreateIndex(
                name: "IX_OfferFeatures_OfferId_FeatureCode",
                schema: "Platform",
                table: "OfferFeatures",
                columns: new[] { "OfferId", "FeatureCode" });
        }
    }
}
