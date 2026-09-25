using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGrossAmountToContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add GrossAmount column with default 0
            migrationBuilder.AddColumn<decimal>(
                name: "GrossAmount",
                schema: "Platform",
                table: "Contracts",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Backfill GrossAmount for complete contracts (EntitlementSnapshotVersion = 1)
            // using the deterministic formula: GrossAmount = ContractedAmount + DiscountAmount
            migrationBuilder.Sql(@"
                UPDATE [Platform].[Contracts]
                SET [GrossAmount] = [ContractedAmount] + [DiscountAmount]
                WHERE [EntitlementSnapshotVersion] = 1
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrossAmount",
                schema: "Platform",
                table: "Contracts");
        }
    }
}
