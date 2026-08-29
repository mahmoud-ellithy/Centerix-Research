using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2SubscriptionsAndLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantPlans_TenantId",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropIndex(
                name: "IX_PlanFeatures_PlanId",
                schema: "Platform",
                table: "PlanFeatures");

            migrationBuilder.RenameColumn(
                name: "StartsAt",
                schema: "Platform",
                table: "TenantPlans",
                newName: "StartsAtUtc");

            // Legacy EndsAt is the old expiry; it must backfill EffectiveEndsAtUtc (NOT
            // ActivatedAtUtc). It is dropped only after the backfill below.
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivatedAtUtc",
                schema: "Platform",
                table: "TenantPlans",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BaseEndsAtUtc",
                schema: "Platform",
                table: "TenantPlans",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "BonusMonths",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DurationMonths",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveEndsAtUtc",
                schema: "Platform",
                table: "TenantPlans",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "Platform",
                table: "TenantPlans",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotCurrency",
                schema: "Platform",
                table: "TenantPlans",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SnapshotMaxBranches",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotMaxStudents",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotMaxTeachers",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotMaxUsers",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotSmsQuota",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotStorageGb",
                schema: "Platform",
                table: "TenantPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BonusMonths",
                schema: "Platform",
                table: "Plans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                schema: "Platform",
                table: "Plans",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "Platform",
                table: "Plans",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMonths",
                schema: "Platform",
                table: "Plans",
                type: "int",
                nullable: false,
                defaultValue: 1);

            // ------------------------------------------------------------------
            // Data preservation backfills (existing subscriptions keep their meaning):
            //   * currency default USD, duration 1 month
            //   * BaseEndsAtUtc = StartsAtUtc + DurationMonths (calendar months)
            //   * EffectiveEndsAtUtc = legacy EndsAt when present, else Base + Bonus
            //   * legacy EndsAt dropped only AFTER backfill
            // ------------------------------------------------------------------
            migrationBuilder.Sql("""
                UPDATE p SET [CurrencyCode] = 'USD'
                FROM [Platform].[Plans] p WHERE [CurrencyCode] = '';

                UPDATE tp SET [SnapshotCurrency] = 'USD', [DurationMonths] = CASE WHEN [DurationMonths] = 0 THEN 1 ELSE [DurationMonths] END
                FROM [Platform].[TenantPlans] tp WHERE [SnapshotCurrency] = '';

                UPDATE tp SET
                    [BaseEndsAtUtc] = DATEADD(MONTH, CASE WHEN [DurationMonths] = 0 THEN 1 ELSE [DurationMonths] END, [StartsAtUtc])
                FROM [Platform].[TenantPlans] tp
                WHERE [BaseEndsAtUtc] = '0001-01-01T00:00:00.000';

                UPDATE tp SET
                    [EffectiveEndsAtUtc] = CASE
                        WHEN tp.[EndsAt] IS NOT NULL THEN tp.[EndsAt]
                        ELSE DATEADD(MONTH, tp.[BonusMonths], DATEADD(MONTH, CASE WHEN tp.[DurationMonths] = 0 THEN 1 ELSE tp.[DurationMonths] END, tp.[StartsAtUtc]))
                    END
                FROM [Platform].[TenantPlans] tp
                WHERE tp.[EffectiveEndsAtUtc] = '0001-01-01T00:00:00.000';

                -- Phase 2 security boundary: tenant roles must not retain commercial
                -- subscription-management grants in EXISTING databases.
                DELETE rp
                FROM [Platform].[RolePermissions] rp
                JOIN [dbo].[AspNetRoles] r ON r.[Id] = rp.[RoleId]
                JOIN [Platform].[Permissions] p ON p.[Id] = rp.[PermissionId]
                WHERE p.[Code] IN ('TenantPlans.Create', 'TenantPlans.Update', 'TenantPlans.Delete')
                  AND r.[NormalizedName] IN ('TENANTADMIN', 'TENANTUSER');
                """);

            migrationBuilder.DropColumn(
                name: "EndsAt",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.CreateTable(
                name: "TenantPlanFeatures",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeatureCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPlanFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantPlanFeatures_TenantPlans_TenantPlanId",
                        column: x => x.TenantPlanId,
                        principalSchema: "Platform",
                        principalTable: "TenantPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlans_EffectiveEndsAtUtc",
                schema: "Platform",
                table: "TenantPlans",
                column: "EffectiveEndsAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlans_TenantId_Status",
                schema: "Platform",
                table: "TenantPlans",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantPlans_TenantId_NonTerminalStatus",
                schema: "Platform",
                table: "TenantPlans",
                column: "TenantId",
                unique: true,
                filter: "[Status] IN (1, 4)");

            migrationBuilder.CreateIndex(
                name: "UX_TenantLimitOverrides_TenantId_LimitType",
                schema: "Platform",
                table: "TenantLimitOverrides",
                columns: new[] { "TenantId", "LimitType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PlanFeatures_PlanId_FeatureId",
                schema: "Platform",
                table: "PlanFeatures",
                columns: new[] { "PlanId", "FeatureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlanFeatures_FeatureCode",
                schema: "Platform",
                table: "TenantPlanFeatures",
                column: "FeatureCode");

            migrationBuilder.CreateIndex(
                name: "UX_TenantPlanFeatures_PlanId_FeatureCode",
                schema: "Platform",
                table: "TenantPlanFeatures",
                columns: new[] { "TenantPlanId", "FeatureCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantPlanFeatures",
                schema: "Platform");

            migrationBuilder.DropIndex(
                name: "IX_TenantPlans_EffectiveEndsAtUtc",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropIndex(
                name: "IX_TenantPlans_TenantId_Status",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropIndex(
                name: "UX_TenantPlans_TenantId_NonTerminalStatus",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropIndex(
                name: "UX_TenantLimitOverrides_TenantId_LimitType",
                schema: "Platform",
                table: "TenantLimitOverrides");

            migrationBuilder.DropIndex(
                name: "UX_PlanFeatures_PlanId_FeatureId",
                schema: "Platform",
                table: "PlanFeatures");

            // Restore legacy EndsAt from the effective end date BEFORE dropping it.
            migrationBuilder.AddColumn<DateTime>(
                name: "EndsAt",
                schema: "Platform",
                table: "TenantPlans",
                type: "datetime2",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE tp SET [EndsAt] = tp.[EffectiveEndsAtUtc]
                FROM [Platform].[TenantPlans] tp;
                """);

            migrationBuilder.DropColumn(
                name: "BaseEndsAtUtc",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "BonusMonths",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "DurationMonths",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "EffectiveEndsAtUtc",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "SnapshotCurrency",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "SnapshotMaxBranches",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "SnapshotMaxStudents",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "SnapshotMaxTeachers",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "SnapshotMaxUsers",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "SnapshotSmsQuota",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "SnapshotStorageGb",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropColumn(
                name: "BonusMonths",
                schema: "Platform",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                schema: "Platform",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "Platform",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "DurationMonths",
                schema: "Platform",
                table: "Plans");

            migrationBuilder.RenameColumn(
                name: "StartsAtUtc",
                schema: "Platform",
                table: "TenantPlans",
                newName: "StartsAt");

            migrationBuilder.DropColumn(
                name: "ActivatedAtUtc",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlans_TenantId",
                schema: "Platform",
                table: "TenantPlans",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanFeatures_PlanId",
                schema: "Platform",
                table: "PlanFeatures",
                column: "PlanId");
        }
    }
}
