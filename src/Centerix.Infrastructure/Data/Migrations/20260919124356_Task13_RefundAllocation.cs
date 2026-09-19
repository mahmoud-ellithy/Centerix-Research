using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task13_RefundAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefundAllocations",
                schema: "Platform",
                columns: table => new
                {
                    RefundAllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RefundId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    PaymentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundAllocations", x => x.RefundAllocationId);
                    table.ForeignKey(
                        name: "FK_RefundAllocations_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "Platform",
                        principalTable: "Payments",
                        principalColumn: "PaymentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundAllocations_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalSchema: "Platform",
                        principalTable: "Refunds",
                        principalColumn: "RefundId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefundAllocations_PaymentId",
                schema: "Platform",
                table: "RefundAllocations",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundAllocations_RefundId",
                schema: "Platform",
                table: "RefundAllocations",
                column: "RefundId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundAllocations_TenantId_PaymentId",
                schema: "Platform",
                table: "RefundAllocations",
                columns: new[] { "TenantId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_RefundAllocations_TenantId_RefundId",
                schema: "Platform",
                table: "RefundAllocations",
                columns: new[] { "TenantId", "RefundId" });

            migrationBuilder.CreateIndex(
                name: "UX_RefundAllocations_TenantId_RefundId_PaymentId",
                schema: "Platform",
                table: "RefundAllocations",
                columns: new[] { "TenantId", "RefundId", "PaymentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefundAllocations",
                schema: "Platform");
        }
    }
}
