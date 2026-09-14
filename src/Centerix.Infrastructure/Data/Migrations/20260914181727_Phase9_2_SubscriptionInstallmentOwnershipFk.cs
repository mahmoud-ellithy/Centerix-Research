using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_2_SubscriptionInstallmentOwnershipFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_Installments_TenantPlans_SubscriptionId",
                schema: "Platform",
                table: "Installments",
                column: "SubscriptionId",
                principalSchema: "Platform",
                principalTable: "TenantPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Installments_TenantPlans_SubscriptionId",
                schema: "Platform",
                table: "Installments");
        }
    }
}
