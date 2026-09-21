using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task18_1_CommercialIntegrityCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "Platform",
                table: "TenantCredits",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BonusMonths",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxBranches",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxStudents",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxTeachers",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxUsers",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SmsQuota",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StorageGb",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ContractFeatures",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeatureCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractFeatures_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "Platform",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_TenantCredits_TenantId_SourceType_SourceId",
                schema: "Platform",
                table: "TenantCredits",
                columns: new[] { "TenantId", "SourceType", "SourceId" },
                unique: true,
                filter: "[SourceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ContractFeatures_ContractId_FeatureCode",
                schema: "Platform",
                table: "ContractFeatures",
                columns: new[] { "ContractId", "FeatureCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractFeatures",
                schema: "Platform");

            migrationBuilder.DropIndex(
                name: "UX_TenantCredits_TenantId_SourceType_SourceId",
                schema: "Platform",
                table: "TenantCredits");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "Platform",
                table: "TenantCredits");

            migrationBuilder.DropColumn(
                name: "BonusMonths",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "MaxBranches",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "MaxStudents",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "MaxTeachers",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "MaxUsers",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SmsQuota",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "StorageGb",
                schema: "Platform",
                table: "Contracts");
        }
    }
}
