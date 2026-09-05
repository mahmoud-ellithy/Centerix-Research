using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAllocationIdToLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PaymentAllocationId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_TenantId_PaymentAllocationId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "PaymentAllocationId" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomerLedgerEntries_SettlementByAllocation",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "PaymentAllocationId", "EntryType" },
                unique: true,
                filter: "[EntryType] = 'PaymentSettlement' AND [PaymentAllocationId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerLedgerEntries_TenantId_PaymentAllocationId",
                schema: "Platform",
                table: "CustomerLedgerEntries");

            migrationBuilder.DropIndex(
                name: "UX_CustomerLedgerEntries_SettlementByAllocation",
                schema: "Platform",
                table: "CustomerLedgerEntries");

            migrationBuilder.DropColumn(
                name: "PaymentAllocationId",
                schema: "Platform",
                table: "CustomerLedgerEntries");
        }
    }
}
