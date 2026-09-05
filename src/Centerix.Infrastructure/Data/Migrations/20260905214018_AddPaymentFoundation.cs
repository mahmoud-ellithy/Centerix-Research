using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerLedgerEntries",
                schema: "Platform",
                columns: table => new
                {
                    LedgerEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    RunningBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerLedgerEntries", x => x.LedgerEntryId);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "Platform",
                columns: table => new
                {
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExternalReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.PaymentId);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                schema: "Platform",
                columns: table => new
                {
                    PaymentAllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AllocatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => x.PaymentAllocationId);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "Platform",
                        principalTable: "Invoices",
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "Platform",
                        principalTable: "Payments",
                        principalColumn: "PaymentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentReceipts",
                schema: "Platform",
                columns: table => new
                {
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReceipts", x => x.ReceiptId);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "Platform",
                        principalTable: "Payments",
                        principalColumn: "PaymentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_TenantId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_TenantId_InvoiceId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_TenantId_PaymentId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_TenantId_RecordedAtUtc",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_InvoiceId",
                schema: "Platform",
                table: "PaymentAllocations",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_PaymentId",
                schema: "Platform",
                table: "PaymentAllocations",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_TenantId",
                schema: "Platform",
                table: "PaymentAllocations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_TenantId_InvoiceId",
                schema: "Platform",
                table: "PaymentAllocations",
                columns: new[] { "TenantId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_TenantId_PaymentId",
                schema: "Platform",
                table: "PaymentAllocations",
                columns: new[] { "TenantId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_TenantId",
                schema: "Platform",
                table: "PaymentReceipts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_TenantId_IssuedAtUtc",
                schema: "Platform",
                table: "PaymentReceipts",
                columns: new[] { "TenantId", "IssuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentReceipts_PaymentId",
                schema: "Platform",
                table: "PaymentReceipts",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PaymentReceipts_ReceiptNumber",
                schema: "Platform",
                table: "PaymentReceipts",
                column: "ReceiptNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ExternalReference",
                schema: "Platform",
                table: "Payments",
                column: "ExternalReference");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId",
                schema: "Platform",
                table: "Payments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId_CompletedAtUtc",
                schema: "Platform",
                table: "Payments",
                columns: new[] { "TenantId", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId_Status",
                schema: "Platform",
                table: "Payments",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Payments_PaymentNumber",
                schema: "Platform",
                table: "Payments",
                column: "PaymentNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerLedgerEntries",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PaymentAllocations",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PaymentReceipts",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "Platform");
        }
    }
}
