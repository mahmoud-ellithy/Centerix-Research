using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContractBenefitsGiftsHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveredBy",
                schema: "Platform",
                table: "ContractBenefits",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "EligibilityStatus",
                schema: "Platform",
                table: "ContractBenefits",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<DateTime>(
                name: "EligibleAtUtc",
                schema: "Platform",
                table: "ContractBenefits",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractBenefits_ContractId_EligibilityStatus",
                schema: "Platform",
                table: "ContractBenefits",
                columns: new[] { "ContractId", "EligibilityStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContractBenefits_ContractId_EligibilityStatus",
                schema: "Platform",
                table: "ContractBenefits");

            migrationBuilder.DropColumn(
                name: "DeliveredBy",
                schema: "Platform",
                table: "ContractBenefits");

            migrationBuilder.DropColumn(
                name: "EligibilityStatus",
                schema: "Platform",
                table: "ContractBenefits");

            migrationBuilder.DropColumn(
                name: "EligibleAtUtc",
                schema: "Platform",
                table: "ContractBenefits");
        }
    }
}
