using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundSettlementUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RefundId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustomerLedgerEntries_SettlementByRefund",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "RefundId", "EntryType" },
                unique: true,
                filter: "[EntryType] = 'RefundSettlement' AND [RefundId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CustomerLedgerEntries_SettlementByRefund",
                schema: "Platform",
                table: "CustomerLedgerEntries");

            migrationBuilder.DropColumn(
                name: "RefundId",
                schema: "Platform",
                table: "CustomerLedgerEntries");
        }
    }
}
