namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Billing.Credits.Enums;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Task 18.4.2 policy — value already converted into a SubscriptionChange credit must
/// not be refunded again as cash.
///
/// <para>
/// <c>ChangeSubscriptionPlanCommand</c> issues the SubscriptionChange credit with
/// <c>SourceId = old subscription id</c>, and the old subscription carries
/// <c>ContractId = old contract id</c>. The contract → credit link is therefore
/// <c>TenantPlan.ContractId</c> → <c>TenantCredit.SourceId</c> (tenant- and
/// currency-scoped, so cross-tenant and cross-contract rows never leak in).
/// </para>
///
/// <para>
/// The FULL issued amount is returned, not just the unconsumed <c>RemainingAmount</c>:
/// the converted value is already recognised economically — as an available credit
/// balance while unconsumed, and as settlement of the new invoice once consumed — and
/// neither form may be paid out as cash a second time.
/// </para>
/// </summary>
internal static class IssuedSubscriptionChangeCredit
{
    /// <summary>
    /// Sums the SubscriptionChange credits already issued for the subscriptions attached
    /// to <paramref name="contractId"/> within <paramref name="tenantId"/> and
    /// <paramref name="currencyCode"/>. Returns 0 when the contract has no plan-change credit.
    /// </summary>
    public static async Task<decimal> GetIssuedAmountAsync(
        IAppDbContext dbContext,
        string tenantId,
        Guid contractId,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        var subscriptionIds = dbContext.TenantPlans
            .Where(tp => tp.TenantId == tenantId && tp.ContractId == contractId)
            .Select(tp => tp.Id);

        return await dbContext.TenantCredits
            .Where(tc => tc.TenantId == tenantId
                      && tc.SourceType == CreditSourceType.SubscriptionChange
                      && tc.SourceId != null
                      && subscriptionIds.Contains(tc.SourceId.Value)
                      && tc.CurrencyCode == currencyCode)
            .SumAsync(tc => tc.Amount, cancellationToken);
    }
}
