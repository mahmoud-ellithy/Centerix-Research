using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task21_FinalInvoiceIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_BillingCycleId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.CreateIndex(
                name: "UX_Invoices_BillingCycleId",
                schema: "Platform",
                table: "Invoices",
                column: "BillingCycleId",
                unique: true,
                filter: "[BillingCycleId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_BillingCycles_BillingCycleId",
                schema: "Platform",
                table: "Invoices",
                column: "BillingCycleId",
                principalSchema: "Platform",
                principalTable: "BillingCycles",
                principalColumn: "BillingCycleId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Contracts_ContractId",
                schema: "Platform",
                table: "Invoices",
                column: "ContractId",
                principalSchema: "Platform",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_TenantPlans_SubscriptionId",
                schema: "Platform",
                table: "Invoices",
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
                name: "FK_Invoices_BillingCycles_BillingCycleId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Contracts_ContractId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_TenantPlans_SubscriptionId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "UX_Invoices_BillingCycleId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_BillingCycleId",
                schema: "Platform",
                table: "Invoices",
                column: "BillingCycleId");
        }
    }
}
