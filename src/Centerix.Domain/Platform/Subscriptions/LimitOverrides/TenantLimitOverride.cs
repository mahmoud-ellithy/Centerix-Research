namespace Centerix.Domain.Platform.Subscriptions.LimitOverrides;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class TenantLimitOverride : AuditableEntity<Guid>
{
    public string LimitType { get; private set; } = default!;
    public int OverrideValue { get; private set; }
    public string? Reason { get; private set; }

    private TenantLimitOverride() { }

    private TenantLimitOverride(
        Guid id,
        string limitType,
        int overrideValue,
        string? reason)
        : base(id)
    {
        LimitType = limitType;
        OverrideValue = overrideValue;
        Reason = reason;
    }

    public static Result<TenantLimitOverride> Create(
        Guid id,
        string limitType,
        int overrideValue,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(limitType))
            return TenantLimitOverrideErrors.LimitTypeRequired;

        return new TenantLimitOverride(id, limitType, overrideValue, reason);
    }
}
