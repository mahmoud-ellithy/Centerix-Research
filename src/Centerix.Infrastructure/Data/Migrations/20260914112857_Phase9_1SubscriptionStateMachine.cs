using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_1SubscriptionStateMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_TenantPlans_TenantId_NonTerminalStatus",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.CreateTable(
                name: "SubscriptionPolicies",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GracePeriodDays = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_TenantPlans_TenantId_NonTerminalStatus",
                schema: "Platform",
                table: "TenantPlans",
                column: "TenantId",
                unique: true,
                filter: "[Status] IN (1, 4, 5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionPolicies",
                schema: "Platform");

            migrationBuilder.DropIndex(
                name: "UX_TenantPlans_TenantId_NonTerminalStatus",
                schema: "Platform",
                table: "TenantPlans");

            migrationBuilder.CreateIndex(
                name: "UX_TenantPlans_TenantId_NonTerminalStatus",
                schema: "Platform",
                table: "TenantPlans",
                column: "TenantId",
                unique: true,
                filter: "[Status] IN (1, 4)");
        }
    }
}
