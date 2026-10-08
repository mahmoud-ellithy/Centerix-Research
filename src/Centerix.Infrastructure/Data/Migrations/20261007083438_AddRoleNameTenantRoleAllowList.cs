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
            // F5 (fail-closed correction): this migration MUST NEVER delete membership rows.
            // If legacy/non-canonical RoleName rows exist, the migration FAILS here — before
            // the CHECK constraint is added — with an operator-actionable message that reports
            // how many rows are affected and a sample identifying them (UserId/TenantId/RoleName).
            // The operator remediates explicitly (UPDATE the RoleName to a canonical value or
            // DELETE the row as a deliberate, auditable action) and re-runs migrations.
            // The predicate below is mirrored 1:1 from the CHECK constraint (same NOT IN list,
            // same explicit case-sensitive collation). NOT IN is consistent for trailing-space
            // values (SQL string comparison pads) and for NULL (the column is NOT NULL, and
            // NULL NOT IN (...) is UNKNOWN → not flagged — matching the CHECK, which also
            // rejects NULL only via the NOT NULL column constraint already in place).
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [Platform].[TenantMemberships] " +
                "WHERE [RoleName] COLLATE Latin1_General_CS_AS NOT IN ('TenantAdmin', 'TenantUser')) " +
                "BEGIN " +
                "DECLARE @BadCount int = (SELECT COUNT(*) FROM [Platform].[TenantMemberships] " +
                "WHERE [RoleName] COLLATE Latin1_General_CS_AS NOT IN ('TenantAdmin', 'TenantUser')); " +
                "DECLARE @Sample nvarchar(1500) = (SELECT STRING_AGG(" +
                "CONCAT('[UserId=', CAST([UserId] AS nvarchar(450)), ';TenantId=', [TenantId], ';RoleName=', [RoleName], ']'), ' | ') " +
                "FROM (SELECT TOP (10) [UserId], [TenantId], [RoleName] FROM [Platform].[TenantMemberships] " +
                "WHERE [RoleName] COLLATE Latin1_General_CS_AS NOT IN ('TenantAdmin', 'TenantUser') " +
                "ORDER BY [TenantId], [UserId]) AS Bad); " +
                "DECLARE @Msg nvarchar(2048) = CONCAT(" +
                "'Migration AddRoleNameTenantRoleAllowList refused: ', @BadCount, " +
                "' row(s) in [Platform].[TenantMemberships] carry non-canonical RoleName values. '," +
                "'The CK_TenantMemberships_RoleName_TenantRoleAllowList constraint was NOT applied and no rows were deleted or modified. '," +
                "'Remediate explicitly (UPDATE RoleName to ''TenantAdmin''/''TenantUser'' or DELETE the row as a deliberate operator action), '," +
                "'then re-run migrations. Sample (up to 10): ', LEFT(@Sample, 1200)); " +
                "THROW 51000, @Msg, 1; " +
                "END");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantMemberships_RoleName_TenantRoleAllowList",
                schema: "Platform",
                table: "TenantMemberships",
                sql: "[RoleName] COLLATE Latin1_General_CS_AS IN ('TenantAdmin', 'TenantUser')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down only drops the constraint. Rows are never deleted or modified by Up, so
            // there is nothing to restore here.
            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantMemberships_RoleName_TenantRoleAllowList",
                schema: "Platform",
                table: "TenantMemberships");
        }
    }
}
