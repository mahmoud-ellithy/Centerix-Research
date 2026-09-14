namespace Centerix.Domain.Platform.Subscriptions;

using Centerix.Domain.Common.Results;

public static class SubscriptionPolicyErrors
{
    public static Error GracePeriodCannotBeNegative =>
        Error.Validation("SubscriptionPolicy.GracePeriod_Negative", "Grace period days cannot be negative.");

    public static Error NotFound =>
        Error.NotFound("SubscriptionPolicy.NotFound", "No subscription policy configuration was found.");
}
