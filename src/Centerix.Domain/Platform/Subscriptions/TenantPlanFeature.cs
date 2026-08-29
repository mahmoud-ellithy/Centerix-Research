namespace Centerix.Domain.Platform.Subscriptions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Immutable per-subscription FEATURE ENTITLEMENT snapshot. Codes are copied from the plan's
/// PlanFeature rows when the subscription is created, so later plan/feature changes never alter
/// what an existing tenant owns. Stored as the feature CODE (not FK) deliberately: entitlement
/// history must survive catalog reorganizations; uniqueness is enforced per subscription.
/// </summary>
public class TenantPlanFeature : GlobalAuditableEntity<Guid>
{
    public Guid TenantPlanId { get; private set; }
    public string FeatureCode { get; private set; } = default!;

    public TenantPlan TenantPlan { get; private set; } = default!;

    private TenantPlanFeature() { }

    private TenantPlanFeature(Guid id, Guid tenantPlanId, string featureCode) : base(id)
    {
        TenantPlanId = tenantPlanId;
        FeatureCode = featureCode;
    }

    internal static TenantPlanFeature Create(Guid tenantPlanId, string featureCode)
        => new(Guid.NewGuid(), tenantPlanId, featureCode.Trim());
}
