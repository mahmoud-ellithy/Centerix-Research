using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractBenefitFulfillmentStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add new column as nullable so the migration can populate it
            //    from existing data without violating NOT NULL.
            migrationBuilder.AddColumn<byte>(
                name: "FulfillmentStatus",
                schema: "Platform",
                table: "ContractBenefits",
                type: "tinyint",
                nullable: true);

            // 2. Semantic preservation: rows with the pre-existing IsGranted=true
            //    flag represent physical gifts that were already handed over.
            //    Map them to FulfillmentStatus=Delivered (2). All other rows
            //    (IsGranted=false or null) → Pending (0).
            migrationBuilder.Sql(@"
                UPDATE [Platform].[ContractBenefits]
                SET [FulfillmentStatus] = CASE
                        WHEN [IsGranted] = 1 THEN 2  -- Delivered
                        ELSE 0                       -- Pending
                    END
                WHERE [FulfillmentStatus] IS NULL;
            ");

            // 3. Tighten FulfillmentStatus to NOT NULL now that every row has a value.
            migrationBuilder.AlterColumn<byte>(
                name: "FulfillmentStatus",
                schema: "Platform",
                table: "ContractBenefits",
                type: "tinyint",
                nullable: false,
                oldClrType: typeof(byte),
                oldType: "tinyint",
                oldNullable: true,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAtUtc",
                schema: "Platform",
                table: "ContractBenefits",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantedBy",
                schema: "Platform",
                table: "ContractBenefits",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractBenefits_ContractId_FulfillmentStatus",
                schema: "Platform",
                table: "ContractBenefits",
                columns: new[] { "ContractId", "FulfillmentStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContractBenefits_ContractId_FulfillmentStatus",
                schema: "Platform",
                table: "ContractBenefits");

            migrationBuilder.DropColumn(
                name: "DeliveredAtUtc",
                schema: "Platform",
                table: "ContractBenefits");

            migrationBuilder.DropColumn(
                name: "FulfillmentStatus",
                schema: "Platform",
                table: "ContractBenefits");

            migrationBuilder.DropColumn(
                name: "GrantedBy",
                schema: "Platform",
                table: "ContractBenefits");
        }
    }
}
