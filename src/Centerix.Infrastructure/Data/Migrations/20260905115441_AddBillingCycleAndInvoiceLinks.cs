using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingCycleAndInvoiceLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BillingCycleId",
                schema: "Platform",
                table: "Invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                schema: "Platform",
                table: "Invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubscriptionId",
                schema: "Platform",
                table: "Invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingCycles",
                schema: "Platform",
                columns: table => new
                {
                    BillingCycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingCycles", x => x.BillingCycleId);
                    table.ForeignKey(
                        name: "FK_BillingCycles_TenantPlans_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "Platform",
                        principalTable: "TenantPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_BillingCycleId",
                schema: "Platform",
                table: "Invoices",
                column: "BillingCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ContractId",
                schema: "Platform",
                table: "Invoices",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_SubscriptionId",
                schema: "Platform",
                table: "Invoices",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingCycles_SubscriptionId",
                schema: "Platform",
                table: "BillingCycles",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingCycles_SubscriptionId_Period",
                schema: "Platform",
                table: "BillingCycles",
                columns: new[] { "SubscriptionId", "PeriodStart", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingCycles_TenantId",
                schema: "Platform",
                table: "BillingCycles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingCycles_TenantId_Status",
                schema: "Platform",
                table: "BillingCycles",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingCycles",
                schema: "Platform");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_BillingCycleId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_ContractId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_SubscriptionId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BillingCycleId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ContractId",
                schema: "Platform",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                schema: "Platform",
                table: "Invoices");
        }
    }
}
