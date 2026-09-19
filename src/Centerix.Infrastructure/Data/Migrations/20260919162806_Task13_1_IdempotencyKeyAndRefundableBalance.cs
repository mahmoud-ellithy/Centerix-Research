using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task13_1_IdempotencyKeyAndRefundableBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "Platform",
                table: "Refunds",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Refunds_TenantId_IdempotencyKey",
                schema: "Platform",
                table: "Refunds",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Refunds_TenantId_IdempotencyKey",
                schema: "Platform",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "Platform",
                table: "Refunds");
        }
    }
}
