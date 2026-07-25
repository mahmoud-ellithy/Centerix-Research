using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Drops DeletedAtUtc and DeletedBy columns from tables whose entities now inherit
    /// from plain AuditableEntity (no soft-delete support). Only Students and Branches
    /// retain these columns because they inherit from SoftDeletableEntity.
    /// </summary>
    public partial class DropSoftDeleteFromNonSoftDeletableTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Plans – reference table, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "Plans");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "Plans");

            // Features – reference table, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "Features");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "Features");

            // PlanFeatures – junction table, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "PlanFeatures");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "PlanFeatures");

            // Permissions – reference table, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "Permissions");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "Permissions");

            // TenantPlans – subscription record, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "TenantPlans");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "TenantPlans");

            // TenantBilling – billing record, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "TenantBilling");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "TenantBilling");

            // TenantCRMLeads – CRM lead record, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "TenantCRMLeads");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "TenantCRMLeads");

            // RefreshTokens – auth token, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "RefreshTokens");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "RefreshTokens");

            // AuditLogs – append-only audit trail, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "AuditLogs");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "AuditLogs");

            // PlatformAuditLog – append-only audit trail, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "PlatformAuditLog");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "PlatformAuditLog");

            // AttendanceLogs – append-only log, no soft-delete needed
            migrationBuilder.DropColumn(name: "DeletedAtUtc", schema: "Platform", table: "AttendanceLogs");
            migrationBuilder.DropColumn(name: "DeletedBy", schema: "Platform", table: "AttendanceLogs");

            // AcademicStages – lookup table, no soft-delete needed
            // AcademicYears – lookup table, no soft-delete needed
            // (These never had the columns in the regenerated AddStudentsEducationModule migration,
            //  so they only have them in older database instances where the original migration ran.)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Note: Down() only restores column structure, NOT data.
            // Data loss is expected and intentional — these columns were unused.

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "Plans",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "Plans",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "Features",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "Features",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "PlanFeatures",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "PlanFeatures",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "Permissions",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "Permissions",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "TenantPlans",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "TenantPlans",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "TenantBilling",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "TenantBilling",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "TenantCRMLeads",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "TenantCRMLeads",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "RefreshTokens",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "RefreshTokens",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "AuditLogs",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "AuditLogs",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "PlatformAuditLog",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "PlatformAuditLog",
                type: "nvarchar(max)", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc", schema: "Platform", table: "AttendanceLogs",
                type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedBy", schema: "Platform", table: "AttendanceLogs",
                type: "nvarchar(max)", nullable: true);
        }
    }
}
