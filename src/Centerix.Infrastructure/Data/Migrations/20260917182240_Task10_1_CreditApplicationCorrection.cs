using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task10_1_CreditApplicationCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedToInvoiceId",
                schema: "Platform",
                table: "TenantCredits");

            migrationBuilder.DropColumn(
                name: "AppliedToInvoiceLineId",
                schema: "Platform",
                table: "TenantCredits");

            migrationBuilder.AddColumn<decimal>(
                name: "RemainingAmount",
                schema: "Platform",
                table: "TenantCredits",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "CreditApplicationId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CreditApplications",
                schema: "Platform",
                columns: table => new
                {
                    CreditApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditApplications", x => x.CreditApplicationId);
                    table.ForeignKey(
                        name: "FK_CreditApplications_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "Platform",
                        principalTable: "Invoices",
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_TenantId_CreditApplicationId",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "CreditApplicationId" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomerLedgerEntries_UsageByCreditApplication",
                schema: "Platform",
                table: "CustomerLedgerEntries",
                columns: new[] { "TenantId", "CreditApplicationId", "EntryType" },
                unique: true,
                filter: "[EntryType] = 'CreditUsage' AND [CreditApplicationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreditApplications_InvoiceId",
                schema: "Platform",
                table: "CreditApplications",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditApplications_TenantId",
                schema: "Platform",
                table: "CreditApplications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditApplications_TenantId_CreditId",
                schema: "Platform",
                table: "CreditApplications",
                columns: new[] { "TenantId", "CreditId" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditApplications_TenantId_CreditId_InvoiceId",
                schema: "Platform",
                table: "CreditApplications",
                columns: new[] { "TenantId", "CreditId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditApplications_TenantId_InvoiceId",
                schema: "Platform",
                table: "CreditApplications",
                columns: new[] { "TenantId", "InvoiceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditApplications",
                schema: "Platform");

            migrationBuilder.DropIndex(
                name: "IX_CustomerLedgerEntries_TenantId_CreditApplicationId",
                schema: "Platform",
                table: "CustomerLedgerEntries");

            migrationBuilder.DropIndex(
                name: "UX_CustomerLedgerEntries_UsageByCreditApplication",
                schema: "Platform",
                table: "CustomerLedgerEntries");

            migrationBuilder.DropColumn(
                name: "RemainingAmount",
                schema: "Platform",
                table: "TenantCredits");

            migrationBuilder.DropColumn(
                name: "CreditApplicationId",
                schema: "Platform",
                table: "CustomerLedgerEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "AppliedToInvoiceId",
                schema: "Platform",
                table: "TenantCredits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AppliedToInvoiceLineId",
                schema: "Platform",
                table: "TenantCredits",
                type: "uniqueidentifier",
                nullable: true);
        }
    }
}
