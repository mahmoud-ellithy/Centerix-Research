using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceRowVersionAndAllocationIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "Platform",
                table: "Invoices",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "UX_PaymentAllocations_Idempotent",
                schema: "Platform",
                table: "PaymentAllocations",
                columns: new[] { "TenantId", "PaymentId", "InvoiceId", "AllocatedAmount" },
                unique: true,
                filter: "[Status] = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PaymentAllocations_Idempotent",
                schema: "Platform",
                table: "PaymentAllocations");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "Platform",
                table: "Invoices");
        }
    }
}
