using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImplementTenantAndAuditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "TenantCRMLeads");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "TenantBilling");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "PlatformAuditLog");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "Platform",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "Platform",
                table: "PlanFeatures");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "Platform",
                table: "Permissions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "Platform",
                table: "Features");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "TenantPlans",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "TenantPlans",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "TenantPlans",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "TenantCRMLeads",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "TenantCRMLeads",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "TenantBilling",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "TenantBilling",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "RefreshTokens",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "RefreshTokens",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "RefreshTokens",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "PlatformAuditLog",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "PlatformAuditLog",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "Plans",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Plans",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "Plans",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "PlanFeatures",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "PlanFeatures",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "PlanFeatures",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "Permissions",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Permissions",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "Permissions",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "Features",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Features",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "Features",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "AuditLogs",
                newName: "ModifiedAt");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "AuditLogs",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "AuditLogs",
                newName: "CreatedAt");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "TenantPlans",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "TenantPlans",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "TenantPlans",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "datetimeoffset",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "AssignedTo",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "TenantBilling",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "TenantBilling",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "Platform",
                table: "TenantBilling",
                type: "datetimeoffset",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "TenantBilling",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "RolePermissions",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "RefreshTokens",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "RefreshTokens",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "RefreshTokens",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "datetimeoffset",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "Plans",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Plans",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "PlanFeatures",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "PlanFeatures",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "Permissions",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Permissions",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "Features",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Features",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "AuditLogs",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModifiedBy",
                schema: "Platform",
                table: "AuditLogs",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlans_TenantId",
                schema: "Platform",
                table: "TenantPlans",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCRMLeads_TenantId",
                schema: "Platform",
                table: "TenantCRMLeads",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCRMLeads_TenantId_Stage",
                schema: "Platform",
                table: "TenantCRMLeads",
                columns: new[] { "TenantId", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBilling_TenantId",
                schema: "Platform",
                table: "TenantBilling",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLog_TenantId_CreatedAt",
                schema: "Platform",
                table: "PlatformAuditLog",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantPlans_TenantId",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.DropIndex(
                name: "IX_TenantCRMLeads_TenantId",
                schema: "Platform",
                table: "TenantCRMLeads");

            migrationBuilder.DropIndex(
                name: "IX_TenantCRMLeads_TenantId_Stage",
                schema: "Platform",
                table: "TenantCRMLeads");

            migrationBuilder.DropIndex(
                name: "IX_TenantBilling_TenantId",
                schema: "Platform",
                table: "TenantBilling");

            migrationBuilder.DropIndex(
                name: "IX_PlatformAuditLog_TenantId_CreatedAt",
                schema: "Platform",
                table: "PlatformAuditLog");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "TenantPlans",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "TenantPlans",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "Platform",
                table: "TenantPlans",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "TenantCRMLeads",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "TenantCRMLeads",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "TenantBilling",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "TenantBilling",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "RefreshTokens",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "RefreshTokens",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "Platform",
                table: "RefreshTokens",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "PlatformAuditLog",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "PlatformAuditLog",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Plans",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "Plans",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "Platform",
                table: "Plans",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "PlanFeatures",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "PlanFeatures",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "Platform",
                table: "PlanFeatures",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Permissions",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "Permissions",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "Platform",
                table: "Permissions",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Features",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "Features",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "Platform",
                table: "Features",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "AuditLogs",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "AuditLogs",
                newName: "LastModifiedUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "Platform",
                table: "AuditLogs",
                newName: "CreatedAtUtc");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "TenantPlans",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "TenantPlans",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "TenantPlans",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset");

            migrationBuilder.AlterColumn<string>(
                name: "AssignedTo",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "TenantBilling",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "TenantBilling",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                schema: "Platform",
                table: "TenantBilling",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset");

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "TenantBilling",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "TenantBilling",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "RolePermissions",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset");

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "Plans",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Plans",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "Plans",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "PlanFeatures",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "PlanFeatures",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "PlanFeatures",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "Permissions",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Permissions",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "Permissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "Features",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Features",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "Features",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                schema: "Platform",
                table: "AuditLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "AuditLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);
        }
    }
}
