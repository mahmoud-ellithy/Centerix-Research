using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Task18_5_CreditEconomicOriginLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TransferredPaidAmount",
                schema: "Platform",
                table: "TenantCredits",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Task 18.5 — existing-row validation: every pre-existing TenantCredit
            // receives TransferredPaidAmount = 0 (direct-origin classification, the
            // conservative superset), which satisfies the invariant below because the
            // domain never creates a credit with Amount <= 0. The CHECK then enforces
            // the immutable-lineage bound at the database level for all future writes.
            migrationBuilder.Sql("""
                ALTER TABLE [Platform].[TenantCredits]
                    ADD CONSTRAINT [CK_TenantCredits_TransferredPaidAmount_Bounded]
                    CHECK ([TransferredPaidAmount] >= 0 AND [TransferredPaidAmount] <= [Amount]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE [Platform].[TenantCredits]
                    DROP CONSTRAINT [CK_TenantCredits_TransferredPaidAmount_Bounded];
                """);

            migrationBuilder.DropColumn(
                name: "TransferredPaidAmount",
                schema: "Platform",
                table: "TenantCredits");
        }
    }
}
