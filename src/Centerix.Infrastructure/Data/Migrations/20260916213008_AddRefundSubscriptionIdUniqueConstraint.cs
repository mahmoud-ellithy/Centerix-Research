using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundSubscriptionIdUniqueConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Refunds_TenantId_SubscriptionId",
                schema: "Platform",
                table: "Refunds");

            migrationBuilder.CreateIndex(
                name: "UX_Refunds_TenantId_SubscriptionId_OnePerSubscription",
                schema: "Platform",
                table: "Refunds",
                columns: new[] { "TenantId", "SubscriptionId" },
                unique: true,
                filter: "[SubscriptionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Refunds_TenantId_SubscriptionId_OnePerSubscription",
                schema: "Platform",
                table: "Refunds");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_TenantId_SubscriptionId",
                schema: "Platform",
                table: "Refunds",
                columns: new[] { "TenantId", "SubscriptionId" });
        }
    }
}
