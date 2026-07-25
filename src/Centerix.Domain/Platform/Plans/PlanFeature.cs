namespace Centerix.Domain.Platform.Plans;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Features;

public class PlanFeature : GlobalAuditableEntity<int>
{
    public int PlanId { get; private set; }
    public int FeatureId { get; private set; }
    public bool IsEnabled { get; private set; }

    public Plan Plan { get; private set; } = default!;
    public Feature Feature { get; private set; } = default!;

    private PlanFeature() { }

    private PlanFeature(int id, int planId, int featureId, bool isEnabled)
        : base(id)
    {
        PlanId = planId;
        FeatureId = featureId;
        IsEnabled = isEnabled;
    }

    public static Result<PlanFeature> Create(int id, int planId, int featureId, bool isEnabled)
    {
        if (planId <= 0)
            return Error.Validation("PlanFeature.PlanId_Invalid", "Plan ID must be greater than zero");

        if (featureId <= 0)
            return Error.Validation("PlanFeature.FeatureId_Invalid", "Feature ID must be greater than zero");

        return new PlanFeature(id, planId, featureId, isEnabled);
    }

    public Result<Updated> Enable()
    {
        IsEnabled = true;
        return Result.Updated;
    }

    public Result<Updated> Disable()
    {
        IsEnabled = false;
        return Result.Updated;
    }
}
