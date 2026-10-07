using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleNameTenantRoleAllowList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F5: remove pre-existing non-conforming rows BEFORE adding the constraint, with a
            // predicate mirrored 1:1 from the CHECK below (same NOT IN list, same explicit
            // case-sensitive collation). NOT IN is consistent for trailing-space values (SQL
            // string comparison pads) and for NULL (the column is NOT NULL, and NULL NOT IN (...)
            // is UNKNOWN → not deleted — matching the CHECK, which also rejects NULL only via the
            // NOT NULL column constraint already in place).
            migrationBuilder.Sql(
                "DELETE FROM [Platform].[TenantMemberships] " +
                "WHERE [RoleName] COLLATE Latin1_General_CS_AS NOT IN ('TenantAdmin', 'TenantUser')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantMemberships_RoleName_TenantRoleAllowList",
                schema: "Platform",
                table: "TenantMemberships",
                sql: "[RoleName] COLLATE Latin1_General_CS_AS IN ('TenantAdmin', 'TenantUser')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantMemberships_RoleName_TenantRoleAllowList",
                schema: "Platform",
                table: "TenantMemberships");
        }
    }
}
