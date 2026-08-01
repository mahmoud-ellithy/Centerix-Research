namespace Centerix.Domain.Platform.Subscriptions.AddOns;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.AddOns.Enums;

public class AddOnCatalog : GlobalAuditableEntity<int>
{
    public string Code { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string UnitType { get; private set; } = default!;
    public int UnitQuantity { get; private set; }
    public AddOnBillingType BillingType { get; private set; }
    public bool IsActive { get; private set; }

    private readonly List<AddOnPricingTier> _pricingTiers = [];
    public IReadOnlyList<AddOnPricingTier> PricingTiers => _pricingTiers.AsReadOnly();

    private readonly List<TenantAddOn> _tenantAddOns = [];
    public IReadOnlyList<TenantAddOn> TenantAddOns => _tenantAddOns.AsReadOnly();

    private AddOnCatalog() { }

    private AddOnCatalog(
        int id,
        string code,
        string displayName,
        string unitType,
        int unitQuantity,
        AddOnBillingType billingType,
        bool isActive)
        : base(id)
    {
        Code = code;
        DisplayName = displayName;
        UnitType = unitType;
        UnitQuantity = unitQuantity;
        BillingType = billingType;
        IsActive = isActive;
    }

    public static Result<AddOnCatalog> Create(
        int id,
        string code,
        string displayName,
        string unitType,
        int unitQuantity,
        AddOnBillingType billingType,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(code))
            return AddOnCatalogErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return AddOnCatalogErrors.DisplayNameRequired;

        if (string.IsNullOrWhiteSpace(unitType))
            return AddOnCatalogErrors.UnitTypeRequired;

        if (unitQuantity <= 0)
            return AddOnCatalogErrors.InvalidUnitQuantity;

        if (!Enum.IsDefined(billingType))
            return Error.Validation("AddOnCatalog.BillingType_Invalid", "Invalid billing type");

        return new AddOnCatalog(id, code, displayName, unitType, unitQuantity, billingType, isActive);
    }

    public Result<Updated> Deactivate()
    {
        if (!IsActive)
            return AddOnCatalogErrors.AlreadyDeactivated;

        IsActive = false;
        return Result.Updated;
    }

    public Result<Updated> Activate()
    {
        if (IsActive)
            return AddOnCatalogErrors.AlreadyActive;

        IsActive = true;
        return Result.Updated;
    }

    public void AddPricingTier(AddOnPricingTier tier)
    {
        if (_pricingTiers.All(t => t.Id != tier.Id))
        {
            _pricingTiers.Add(tier);
        }
    }

    public void RemovePricingTier(int tierId)
    {
        _pricingTiers.RemoveAll(t => t.Id == tierId);
    }
}
