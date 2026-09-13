using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallmentAndPaymentAllocationInstallmentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PaymentAllocations_Idempotent",
                schema: "Platform",
                table: "PaymentAllocations");

            migrationBuilder.AddColumn<Guid>(
                name: "InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Installments",
                schema: "Platform",
                columns: table => new
                {
                    InstallmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SequenceNumber = table.Column<int>(type: "int", nullable: false),
                    DueDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CoveredPeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CoveredPeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    SettledAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installments", x => x.InstallmentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations",
                column: "InstallmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_TenantId_InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations",
                columns: new[] { "TenantId", "InstallmentId" });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentAllocations_Idempotent",
                schema: "Platform",
                table: "PaymentAllocations",
                columns: new[] { "TenantId", "PaymentId", "InvoiceId", "InstallmentId", "AllocatedAmount" },
                unique: true,
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_Installments_ContractId",
                schema: "Platform",
                table: "Installments",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Installments_InvoiceId",
                schema: "Platform",
                table: "Installments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Installments_SubscriptionId",
                schema: "Platform",
                table: "Installments",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Installments_TenantId",
                schema: "Platform",
                table: "Installments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Installments_TenantId_ContractId",
                schema: "Platform",
                table: "Installments",
                columns: new[] { "TenantId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_Installments_TenantId_DueDateUtc",
                schema: "Platform",
                table: "Installments",
                columns: new[] { "TenantId", "DueDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Installments_TenantId_Status",
                schema: "Platform",
                table: "Installments",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Installments_TenantId_SubscriptionId",
                schema: "Platform",
                table: "Installments",
                columns: new[] { "TenantId", "SubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "UX_Installments_TenantContractSequence",
                schema: "Platform",
                table: "Installments",
                columns: new[] { "TenantId", "ContractId", "SequenceNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentAllocations_Installments_InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations",
                column: "InstallmentId",
                principalSchema: "Platform",
                principalTable: "Installments",
                principalColumn: "InstallmentId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentAllocations_Installments_InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations");

            migrationBuilder.DropTable(
                name: "Installments",
                schema: "Platform");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAllocations_InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAllocations_TenantId_InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations");

            migrationBuilder.DropIndex(
                name: "UX_PaymentAllocations_Idempotent",
                schema: "Platform",
                table: "PaymentAllocations");

            migrationBuilder.DropColumn(
                name: "InstallmentId",
                schema: "Platform",
                table: "PaymentAllocations");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentAllocations_Idempotent",
                schema: "Platform",
                table: "PaymentAllocations",
                columns: new[] { "TenantId", "PaymentId", "InvoiceId", "AllocatedAmount" },
                unique: true,
                filter: "[Status] = 'Active'");
        }
    }
}
