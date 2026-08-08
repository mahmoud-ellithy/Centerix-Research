namespace Centerix.Domain.Platform.Plans;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;

public class Plan : GlobalAuditableEntity<int>
{
    public string Code { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public decimal MonthlyPrice { get; private set; }
    public int MaxStudents { get; private set; }
    public int MaxUsers { get; private set; }
    public int MaxBranches { get; private set; }
    public int MaxTeachers { get; private set; }
    public int StorageGB { get; private set; }
    public int SMSQuota { get; private set; }
    public bool IsActive { get; private set; }

    private readonly List<PlanFeature> _planFeatures = [];
    public IReadOnlyList<PlanFeature> PlanFeatures => _planFeatures.AsReadOnly();

    private readonly List<TenantPlan> _tenantPlans = [];
    public IReadOnlyList<TenantPlan> TenantPlans => _tenantPlans.AsReadOnly();

    private Plan() { }

    private Plan(
        int id,
        string code,
        string displayName,
        decimal monthlyPrice,
        int maxStudents,
        int maxUsers,
        int maxBranches,
        int maxTeachers,
        int storageGB,
        int smsQuota,
        bool isActive)
        : base(id)
    {
        Code = code;
        DisplayName = displayName;
        MonthlyPrice = monthlyPrice;
        MaxStudents = maxStudents;
        MaxUsers = maxUsers;
        MaxBranches = maxBranches;
        MaxTeachers = maxTeachers;
        StorageGB = storageGB;
        SMSQuota = smsQuota;
        IsActive = isActive;
    }

    public static Result<Plan> Create(
        int id,
        string code,
        string displayName,
        decimal monthlyPrice,
        int maxStudents,
        int maxUsers,
        int maxBranches,
        int maxTeachers,
        int storageGB,
        int smsQuota,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(code))
            return PlanErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return PlanErrors.DisplayNameRequired;

        if (monthlyPrice < 0)
            return PlanErrors.InvalidPrice;

        if (maxStudents < 0 || maxUsers < 0 || maxBranches < 0 || maxTeachers < 0 || storageGB < 0 || smsQuota < 0)
            return PlanErrors.InvalidLimits;

        var plan = new Plan(id, code, displayName, monthlyPrice, maxStudents, maxUsers, maxBranches, maxTeachers, storageGB, smsQuota, isActive);

        plan.AddDomainEvent(new Events.PlanCreatedEvent(plan));

        return plan;
    }

    public Result<Updated> Update(
        string code,
        string displayName,
        decimal monthlyPrice,
        int maxStudents,
        int maxUsers,
        int maxBranches,
        int maxTeachers,
        int storageGB,
        int smsQuota,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(code))
            return PlanErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return PlanErrors.DisplayNameRequired;

        if (monthlyPrice < 0)
            return PlanErrors.InvalidPrice;

        if (maxStudents < 0 || maxUsers < 0 || maxBranches < 0 || maxTeachers < 0 || storageGB < 0 || smsQuota < 0)
            return PlanErrors.InvalidLimits;

        Code = code;
        DisplayName = displayName;
        MonthlyPrice = monthlyPrice;
        MaxStudents = maxStudents;
        MaxUsers = maxUsers;
        MaxBranches = maxBranches;
        MaxTeachers = maxTeachers;
        StorageGB = storageGB;
        SMSQuota = smsQuota;
        IsActive = isActive;

        return Result.Updated;
    }

    public Result<Updated> Deactivate()
    {
        if (!IsActive)
            return PlanErrors.AlreadyDeactivated;

        IsActive = false;
        AddDomainEvent(new Events.PlanDeactivatedEvent(Id));
        return Result.Updated;
    }

    public Result<Updated> Activate()
    {
        if (IsActive)
            return PlanErrors.AlreadyActive;

        IsActive = true;
        AddDomainEvent(new Events.PlanActivatedEvent(Id));
        return Result.Updated;
    }

    public void AddPlanFeature(PlanFeature feature)
    {
        if (!_planFeatures.Any(f => f.FeatureId == feature.FeatureId))
        {
            _planFeatures.Add(feature);
        }
    }

    public void RemovePlanFeature(int featureId)
    {
        _planFeatures.RemoveAll(f => f.FeatureId == featureId);
    }
}
