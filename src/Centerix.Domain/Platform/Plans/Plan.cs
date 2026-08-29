namespace Centerix.Domain.Platform.Plans;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;

public class Plan : GlobalAuditableEntity<int>
{
    public string Code { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string? Description { get; private set; }

    /// <summary>ISO-4217 currency the price is expressed in. Snapshotted onto each subscription.</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>Commercial base term in calendar months granted per purchase/renewal period.</summary>
    public int DurationMonths { get; private set; }

    /// <summary>Promotional bonus months ADDED to every new subscription's term at purchase time.</summary>
    public int BonusMonths { get; private set; }

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
        bool isActive,
        string currencyCode,
        int durationMonths,
        int bonusMonths,
        string? description = null)
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
        CurrencyCode = currencyCode;
        DurationMonths = durationMonths;
        BonusMonths = bonusMonths;
        Description = description;
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
        bool isActive = true,
        string? description = null,
        string currencyCode = "USD",
        int durationMonths = 1,
        int bonusMonths = 0)
    {
        if (string.IsNullOrWhiteSpace(code))
            return PlanErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return PlanErrors.DisplayNameRequired;

        if (monthlyPrice < 0)
            return PlanErrors.InvalidPrice;

        if (maxStudents < 0 || maxUsers < 0 || maxBranches < 0 || maxTeachers < 0 || storageGB < 0 || smsQuota < 0)
            return PlanErrors.InvalidLimits;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return PlanErrors.InvalidCurrency;

        if (durationMonths <= 0)
            return PlanErrors.InvalidDuration;

        if (bonusMonths < 0)
            return PlanErrors.InvalidBonus;

        var plan = new Plan(
            id, code, displayName, monthlyPrice, maxStudents, maxUsers, maxBranches, maxTeachers,
            storageGB, smsQuota, isActive,
            currencyCode.Trim().ToUpperInvariant(), durationMonths, bonusMonths, description);

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
        bool isActive,
        string? description = null,
        string? currencyCode = null,
        int? durationMonths = null,
        int? bonusMonths = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            return PlanErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return PlanErrors.DisplayNameRequired;

        if (monthlyPrice < 0)
            return PlanErrors.InvalidPrice;

        if (maxStudents < 0 || maxUsers < 0 || maxBranches < 0 || maxTeachers < 0 || storageGB < 0 || smsQuota < 0)
            return PlanErrors.InvalidLimits;

        var effectiveCurrency = currencyCode ?? CurrencyCode;
        if (string.IsNullOrWhiteSpace(effectiveCurrency) || effectiveCurrency.Trim().Length != 3)
            return PlanErrors.InvalidCurrency;

        var effectiveDuration = durationMonths ?? DurationMonths;
        if (effectiveDuration <= 0)
            return PlanErrors.InvalidDuration;

        var effectiveBonus = bonusMonths ?? BonusMonths;
        if (effectiveBonus < 0)
            return PlanErrors.InvalidBonus;

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
        Description = description;
        CurrencyCode = effectiveCurrency.Trim().ToUpperInvariant();
        DurationMonths = effectiveDuration;
        BonusMonths = effectiveBonus;

        // NOTE: existing TenantPlan subscriptions keep their purchased snapshot; this update
        // only affects FUTURE subscriptions (see TenantPlan snapshot design).
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
