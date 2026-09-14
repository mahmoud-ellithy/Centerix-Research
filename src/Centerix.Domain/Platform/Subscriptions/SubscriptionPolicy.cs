namespace Centerix.Domain.Platform.Subscriptions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Platform-level subscription policy. Centrally controlled by Platform Admin.
/// Tenant-level users must not configure or override these values.
/// Currently a single-row configuration holding the Grace Period for financial obligations.
/// </summary>
public class SubscriptionPolicy : GlobalAuditableEntity<int>
{
    /// <summary>
    /// Number of days after an installment's DueDateUtc during which the subscription
    /// transitions to PastDue rather than directly to Suspended. Centrally controlled;
    /// tenant-level users must not override this value.
    /// </summary>
    public int GracePeriodDays { get; private set; }

    private SubscriptionPolicy() { }

    private SubscriptionPolicy(int id, int gracePeriodDays) : base(id)
    {
        GracePeriodDays = gracePeriodDays;
    }

    public static Result<SubscriptionPolicy> Create(int id, int gracePeriodDays)
    {
        if (gracePeriodDays < 0)
            return SubscriptionPolicyErrors.GracePeriodCannotBeNegative;

        return new SubscriptionPolicy(id, gracePeriodDays);
    }

    public Result<Updated> UpdateGracePeriodDays(int gracePeriodDays)
    {
        if (gracePeriodDays < 0)
            return SubscriptionPolicyErrors.GracePeriodCannotBeNegative;

        GracePeriodDays = gracePeriodDays;
        return Result.Updated;
    }
}
