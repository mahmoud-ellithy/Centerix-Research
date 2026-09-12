using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChargedMonths",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromotionId",
                schema: "Platform",
                table: "Contracts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionType",
                schema: "Platform",
                table: "Contracts",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Promotions",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Type = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    PlanId = table.Column<int>(type: "int", nullable: false),
                    DurationMonths = table.Column<int>(type: "int", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Percentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    FixedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PromotionalPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ChargedMonths = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Promotions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_DateRange",
                schema: "Platform",
                table: "Promotions",
                columns: new[] { "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_PlanId",
                schema: "Platform",
                table: "Promotions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_Priority",
                schema: "Platform",
                table: "Promotions",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_Status",
                schema: "Platform",
                table: "Promotions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_Status_PlanId_DurationMonths",
                schema: "Platform",
                table: "Promotions",
                columns: new[] { "Status", "PlanId", "DurationMonths" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Promotions",
                schema: "Platform");

            migrationBuilder.DropColumn(
                name: "ChargedMonths",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PromotionId",
                schema: "Platform",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PromotionType",
                schema: "Platform",
                table: "Contracts");
        }
    }
}
