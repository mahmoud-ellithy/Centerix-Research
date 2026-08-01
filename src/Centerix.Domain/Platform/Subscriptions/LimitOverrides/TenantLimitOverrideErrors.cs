namespace Centerix.Domain.Platform.Subscriptions.LimitOverrides;

using Centerix.Domain.Common.Results;

public static class TenantLimitOverrideErrors
{
    public static Error LimitTypeRequired =>
        Error.Validation("TenantLimitOverride.LimitType_Required", "Limit type is required");
}
