using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Remap <c>FreeMonthsBenefits.FulfillmentStatus</c> values to the new shared
    /// <see cref="Centerix.Domain.Platform.Contracts.Enums.FulfillmentStatus"/> numeric
    /// ordering (Pending=0, Granted=1, Delivered=2, AppliedToSubscription=3).
    ///
    /// Pre-Task-E FreeMonths persisted <c>AppliedToSubscription = 2</c>.
    /// Post-Task-E it is <c>3</c> (because Delivered occupies slot 2).
    /// </summary>
    public partial class SyncFreeMonthsBenefitFulfillmentStatusValues : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE [Platform].[FreeMonthsBenefits]
                SET [FulfillmentStatus] = 3
                WHERE [FulfillmentStatus] = 2;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE [Platform].[FreeMonthsBenefits]
                SET [FulfillmentStatus] = 2
                WHERE [FulfillmentStatus] = 3;
            ");
        }
    }
}