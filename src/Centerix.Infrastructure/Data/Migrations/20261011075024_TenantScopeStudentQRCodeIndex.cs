using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantScopeStudentQRCodeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Students_QRCode",
                schema: "Platform",
                table: "Students");

            migrationBuilder.CreateIndex(
                name: "UX_Students_Tenant_QRCode",
                schema: "Platform",
                table: "Students",
                columns: new[] { "TenantId", "QRCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Students_Tenant_QRCode",
                schema: "Platform",
                table: "Students");

            migrationBuilder.CreateIndex(
                name: "UX_Students_QRCode",
                schema: "Platform",
                table: "Students",
                column: "QRCode",
                unique: true);
        }
    }
}
