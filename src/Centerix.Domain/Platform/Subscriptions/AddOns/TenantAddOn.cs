namespace Centerix.Domain.Platform.Subscriptions.AddOns;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.AddOns.Enums;

public class TenantAddOn : AuditableEntity<Guid>
{
    public int AddOnCatalogId { get; private set; }
    public int Quantity { get; private set; }
    public decimal SnapshotUnitPrice { get; private set; }
    public DateTime EffectiveFrom { get; private set; }
    public DateTime? EffectiveTo { get; private set; }
    public TenantAddOnStatus Status { get; private set; }
    public Guid? InvoiceLineId { get; private set; }

    public AddOnCatalog AddOnCatalog { get; private set; } = default!;

    private TenantAddOn() { }

    private TenantAddOn(
        Guid id,
        int addOnCatalogId,
        int quantity,
        decimal snapshotUnitPrice,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        TenantAddOnStatus status,
        Guid? invoiceLineId)
        : base(id)
    {
        AddOnCatalogId = addOnCatalogId;
        Quantity = quantity;
        SnapshotUnitPrice = snapshotUnitPrice;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        Status = status;
        InvoiceLineId = invoiceLineId;
    }

    public static Result<TenantAddOn> Create(
        Guid id,
        int addOnCatalogId,
        int quantity,
        decimal snapshotUnitPrice,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        TenantAddOnStatus status,
        Guid? invoiceLineId = null)
    {
        if (addOnCatalogId <= 0)
            return TenantAddOnErrors.AddOnCatalogIdRequired;

        if (quantity <= 0)
            return TenantAddOnErrors.InvalidQuantity;

        if (snapshotUnitPrice < 0)
            return TenantAddOnErrors.InvalidUnitPrice;

        if (!Enum.IsDefined(status))
            return Error.Validation("TenantAddOn.Status_Invalid", "Invalid tenant add-on status");

        return new TenantAddOn(id, addOnCatalogId, quantity, snapshotUnitPrice, effectiveFrom, effectiveTo, status, invoiceLineId);
    }

    public Result<Updated> Cancel()
    {
        if (Status == TenantAddOnStatus.Cancelled)
            return TenantAddOnErrors.AlreadyCancelled;

        Status = TenantAddOnStatus.Cancelled;

        return Result.Updated;
    }
}
