namespace Centerix.Domain.Platform.Billing.BillingCycles;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;

public static class BillingCycleErrors
{
    public static readonly Error TenantIdRequired =
        Error.Validation("BillingCycle.TenantId_Required", "TenantId is required.");

    public static readonly Error SubscriptionIdRequired =
        Error.Validation("BillingCycle.SubscriptionId_Required", "SubscriptionId is required.");

    public static readonly Error PeriodStartRequired =
        Error.Validation("BillingCycle.PeriodStartRequired", "PeriodStart is required.");

    public static readonly Error PeriodEndRequired =
        Error.Validation("BillingCycle.PeriodEndRequired", "PeriodEnd is required.");

    public static readonly Error InvalidPeriod =
        Error.Validation("BillingCycle.InvalidPeriod", "PeriodEnd must be after PeriodStart.");

    public static readonly Error NoBillablePeriod =
        Error.Validation(
            "BillingCycle.NoBillablePeriod",
            "The billing cycle period contains no paid time inside the subscription's paid term " +
            "(StartsAtUtc to BaseEndsAtUtc); time outside it (before the start, or in bonus months) " +
            "is free and can never be billed.");

    public static Error InvalidStateTransition(BillingCycleStatus current, string attempted) =>
        Error.Conflict("BillingCycle.InvalidStateTransition",
            $"Cannot {attempted} from status '{current}'.");
}
