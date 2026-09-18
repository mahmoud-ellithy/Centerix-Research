using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task12_1_CustomerCreditIdempotencyAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                schema: "Platform",
                table: "TenantCredits",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "EGP");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "Platform",
                table: "CreditApplications",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_CreditApplications_TenantId_IdempotencyKey",
                schema: "Platform",
                table: "CreditApplications",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditApplications_TenantId_IdempotencyKey",
                schema: "Platform",
                table: "CreditApplications");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                schema: "Platform",
                table: "TenantCredits");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "Platform",
                table: "CreditApplications");
        }
    }
}
