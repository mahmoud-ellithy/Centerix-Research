using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PendingChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantBilling",
                schema: "Platform");

            migrationBuilder.AddColumn<decimal>(
                name: "SnapshotPrice",
                schema: "Platform",
                table: "TenantPlans",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<Guid>(
                name: "AssignedTo",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                schema: "Platform",
                table: "Features",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.CreateTable(
                name: "AddOnCatalogs",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UnitType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitQuantity = table.Column<int>(type: "int", nullable: false),
                    BillingType = table.Column<byte>(type: "tinyint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddOnCatalogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                schema: "Platform",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.InvoiceId);
                });

            migrationBuilder.CreateTable(
                name: "PlatformPermissions",
                schema: "Platform",
                columns: table => new
                {
                    PermissionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Module = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformPermissions", x => x.PermissionId);
                });

            migrationBuilder.CreateTable(
                name: "PlatformRoles",
                schema: "Platform",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformRoles", x => x.RoleId);
                });

            migrationBuilder.CreateTable(
                name: "PlatformUsers",
                schema: "Platform",
                columns: table => new
                {
                    PlatformUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Is2FAEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformUsers", x => x.PlatformUserId);
                });

            migrationBuilder.CreateTable(
                name: "TenantCredits",
                schema: "Platform",
                columns: table => new
                {
                    TenantCreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AppliedToInvoiceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReversalOfCreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCredits", x => x.TenantCreditId);
                });

            migrationBuilder.CreateTable(
                name: "TenantLimitOverrides",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LimitType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OverrideValue = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLimitOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantProvisioningJobs",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RetryCount = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantProvisioningJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantReferralCodes",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TimesUsed = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantReferralCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "Platform",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Subdomain = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PrimaryColor = table.Column<string>(type: "nchar(7)", maxLength: 7, nullable: true),
                    Country = table.Column<string>(type: "nchar(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", maxLength: 3, nullable: false),
                    Timezone = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    OwnerFirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OwnerLastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OwnerEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OwnerPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    IsolationMode = table.Column<byte>(type: "tinyint", nullable: false),
                    DatabaseServer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConnectionStringRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CurrentPlanId = table.Column<int>(type: "int", nullable: true),
                    LifecycleStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    SuspendedReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TrialEndsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ValidUpTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "TenantSchemaVersions",
                schema: "Platform",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId1 = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CurrentVersion = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LastMigratedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSchemaVersions", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ValueType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantUsageCounters",
                schema: "Platform",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudentsCount = table.Column<int>(type: "int", nullable: false),
                    UsersCount = table.Column<int>(type: "int", nullable: false),
                    BranchesCount = table.Column<int>(type: "int", nullable: false),
                    TeachersCount = table.Column<int>(type: "int", nullable: false),
                    StorageUsedMB = table.Column<int>(type: "int", nullable: false),
                    SMSUsedThisCycle = table.Column<int>(type: "int", nullable: false),
                    EffectiveMaxStudents = table.Column<int>(type: "int", nullable: false),
                    EffectiveMaxUsers = table.Column<int>(type: "int", nullable: false),
                    EffectiveMaxBranches = table.Column<int>(type: "int", nullable: false),
                    EffectiveMaxTeachers = table.Column<int>(type: "int", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SyncStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsageCounters", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "AddOnPricingTiers",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AddOnCatalogId = table.Column<int>(type: "int", nullable: false),
                    MinQuantity = table.Column<int>(type: "int", nullable: false),
                    MaxQuantity = table.Column<int>(type: "int", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddOnPricingTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AddOnPricingTiers_AddOnCatalogs_AddOnCatalogId",
                        column: x => x.AddOnCatalogId,
                        principalSchema: "Platform",
                        principalTable: "AddOnCatalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantAddOns",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddOnCatalogId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    SnapshotUnitPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    InvoiceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAddOns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantAddOns_AddOnCatalogs_AddOnCatalogId",
                        column: x => x.AddOnCatalogId,
                        principalSchema: "Platform",
                        principalTable: "AddOnCatalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                schema: "Platform",
                columns: table => new
                {
                    InvoiceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ProratedDays = table.Column<int>(type: "int", nullable: true),
                    LineTotal = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.InvoiceLineId);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "Platform",
                        principalTable: "Invoices",
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlatformPayments",
                schema: "Platform",
                columns: table => new
                {
                    PlatformPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    GatewayRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformPayments", x => x.PlatformPaymentId);
                    table.ForeignKey(
                        name: "FK_PlatformPayments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "Platform",
                        principalTable: "Invoices",
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlatformRolePermissions",
                schema: "Platform",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    PermissionId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformRolePermissions", x => new { x.RoleId, x.PermissionId });
                    table.ForeignKey(
                        name: "FK_PlatformRolePermissions_PlatformPermissions_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "Platform",
                        principalTable: "PlatformPermissions",
                        principalColumn: "PermissionId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlatformRolePermissions_PlatformRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "Platform",
                        principalTable: "PlatformRoles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImpersonationLogs",
                schema: "Platform",
                columns: table => new
                {
                    LogId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IPAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImpersonationLogs", x => x.LogId);
                    table.ForeignKey(
                        name: "FK_ImpersonationLogs_PlatformUsers_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalSchema: "Platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "PlatformUserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlatformUserRoles",
                schema: "Platform",
                columns: table => new
                {
                    PlatformUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformUserRoles", x => new { x.PlatformUserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_PlatformUserRoles_PlatformRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "Platform",
                        principalTable: "PlatformRoles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlatformUserRoles_PlatformUsers_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalSchema: "Platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "PlatformUserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantReferrals",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferrerTenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReferredTenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReferralCodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    QualifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RewardType = table.Column<byte>(type: "tinyint", nullable: false),
                    RewardValue = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    RewardAppliedTo = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RewardAppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantReferrals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantReferrals_TenantReferralCodes_ReferralCodeId",
                        column: x => x.ReferralCodeId,
                        principalSchema: "Platform",
                        principalTable: "TenantReferralCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AddOnCatalogs_Code",
                schema: "Platform",
                table: "AddOnCatalogs",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AddOnPricingTiers_AddOnCatalogId",
                schema: "Platform",
                table: "AddOnPricingTiers",
                column: "AddOnCatalogId");

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationLogs_PlatformUserId",
                schema: "Platform",
                table: "ImpersonationLogs",
                column: "PlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationLogs_StartedAt",
                schema: "Platform",
                table: "ImpersonationLogs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationLogs_TenantId",
                schema: "Platform",
                table: "ImpersonationLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_InvoiceId",
                schema: "Platform",
                table: "InvoiceLines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId",
                schema: "Platform",
                table: "Invoices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_PeriodStart_PeriodEnd",
                schema: "Platform",
                table: "Invoices",
                columns: new[] { "TenantId", "PeriodStart", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_Status",
                schema: "Platform",
                table: "Invoices",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Invoices_InvoiceNumber",
                schema: "Platform",
                table: "Invoices",
                column: "InvoiceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformPayments_GatewayRef",
                schema: "Platform",
                table: "PlatformPayments",
                column: "GatewayRef");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformPayments_InvoiceId",
                schema: "Platform",
                table: "PlatformPayments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "UX_PlatformPermissions_Code",
                schema: "Platform",
                table: "PlatformPermissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformRolePermissions_PermissionId",
                schema: "Platform",
                table: "PlatformRolePermissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "UX_PlatformRoles_Code",
                schema: "Platform",
                table: "PlatformRoles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformUserRoles_RoleId",
                schema: "Platform",
                table: "PlatformUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformUsers_IsActive",
                schema: "Platform",
                table: "PlatformUsers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_PlatformUsers_Email",
                schema: "Platform",
                table: "PlatformUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantAddOns_AddOnCatalogId",
                schema: "Platform",
                table: "TenantAddOns",
                column: "AddOnCatalogId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAddOns_TenantId",
                schema: "Platform",
                table: "TenantAddOns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredits_TenantId",
                schema: "Platform",
                table: "TenantCredits",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredits_TenantId_Status",
                schema: "Platform",
                table: "TenantCredits",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLimitOverrides_TenantId",
                schema: "Platform",
                table: "TenantLimitOverrides",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantProvisioningJobs_Status",
                schema: "Platform",
                table: "TenantProvisioningJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TenantProvisioningJobs_TenantId",
                schema: "Platform",
                table: "TenantProvisioningJobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantReferralCodes_Code",
                schema: "Platform",
                table: "TenantReferralCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantReferralCodes_TenantId",
                schema: "Platform",
                table: "TenantReferralCodes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantReferrals_ReferralCodeId",
                schema: "Platform",
                table: "TenantReferrals",
                column: "ReferralCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantReferrals_ReferredTenantId",
                schema: "Platform",
                table: "TenantReferrals",
                column: "ReferredTenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantReferrals_ReferrerTenantId",
                schema: "Platform",
                table: "TenantReferrals",
                column: "ReferrerTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantReferrals_TenantId",
                schema: "Platform",
                table: "TenantReferrals",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_CurrentPlanId",
                schema: "Platform",
                table: "Tenants",
                column: "CurrentPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_IsActive",
                schema: "Platform",
                table: "Tenants",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_LifecycleStatus",
                schema: "Platform",
                table: "Tenants",
                column: "LifecycleStatus");

            migrationBuilder.CreateIndex(
                name: "UX_Tenants_Slug",
                schema: "Platform",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Tenants_Subdomain",
                schema: "Platform",
                table: "Tenants",
                column: "Subdomain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId",
                schema: "Platform",
                table: "TenantSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_Key",
                schema: "Platform",
                table: "TenantSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsageCounters_TenantId",
                schema: "Platform",
                table: "TenantUsageCounters",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AddOnPricingTiers",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "ImpersonationLogs",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "InvoiceLines",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PlatformPayments",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PlatformRolePermissions",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PlatformUserRoles",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantAddOns",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantCredits",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantLimitOverrides",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantProvisioningJobs",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantReferrals",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantSchemaVersions",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantSettings",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantUsageCounters",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Invoices",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PlatformPermissions",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PlatformRoles",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "PlatformUsers",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "AddOnCatalogs",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TenantReferralCodes",
                schema: "Platform");

            migrationBuilder.DropColumn(
                name: "SnapshotPrice",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.AlterColumn<string>(
                name: "AssignedTo",
                schema: "Platform",
                table: "TenantCRMLeads",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "Platform",
                table: "PlatformAuditLog",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                schema: "Platform",
                table: "Features",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "TenantBilling",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<int>(type: "int", nullable: false),
                    AmountEGP = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    InvoiceRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBilling", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantBilling_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "Platform",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBilling_PlanId",
                schema: "Platform",
                table: "TenantBilling",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBilling_TenantId",
                schema: "Platform",
                table: "TenantBilling",
                column: "TenantId");
        }
    }
}
