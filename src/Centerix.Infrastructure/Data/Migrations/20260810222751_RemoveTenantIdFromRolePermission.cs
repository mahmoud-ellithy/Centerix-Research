using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantIdFromRolePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RolePermissions_TenantId_RoleId",
                schema: "Platform",
                table: "RolePermissions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "Platform",
                table: "RolePermissions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "RolePermissions",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_TenantId_RoleId",
                schema: "Platform",
                table: "RolePermissions",
                columns: new[] { "TenantId", "RoleId" });
        }
    }
}
