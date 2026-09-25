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
    ///
    /// <para>
    /// Credits are included if their SourceId subscription's ORIGINAL contract is the refund contract.
    /// Credits from subscriptions whose original contract is a DIFFERENT contract (i.e., the subscription
    /// was created by a plan change) are excluded because their value has already been recognized
    /// through the subscription's own contract.
    /// </para>
    /// </summary>
    public static async Task<decimal> GetIssuedAmountAsync(
        IAppDbContext dbContext,
        string tenantId,
        Guid contractId,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        // Get the subscriptions whose ORIGINAL contract is the refund contract.
        // A subscription's original contract is set when the subscription is first created.
        // Subsequent plan changes create NEW subscriptions with the new contract.
        // So a subscription with ContractId = contractId means its CURRENT contract is contractId,
        // but we need to know if contractId was the ORIGINAL contract for this subscription.
        //
        // The simplest approach: join TenantCredits with TenantPlans on SourceId = TenantPlan.Id,
        // and filter where TenantPlan.ContractId = contractId.
        // This correctly includes credits from subscriptions created directly under contractId,
        // and excludes credits from subscriptions created by plan changes (which have different ContractIds).
        return await dbContext.TenantCredits
            .Where(tc => tc.TenantId == tenantId
                      && tc.SourceType == CreditSourceType.SubscriptionChange
                      && tc.SourceId != null
                      && tc.CurrencyCode == currencyCode
                      && dbContext.TenantPlans.Any(tp =>
                          tp.Id == tc.SourceId.Value
                          && tp.TenantId == tenantId
                          && tp.ContractId == contractId))
            .SumAsync(tc => tc.Amount, cancellationToken);
    }
}
