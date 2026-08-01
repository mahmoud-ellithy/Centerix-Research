namespace Centerix.Domain.Platform.Subscriptions.AddOns;

using Centerix.Domain.Common;

public class AddOnPricingTier : GlobalAuditableEntity<int>
{
    public int AddOnCatalogId { get; private set; }
    public int MinQuantity { get; private set; }
    public int? MaxQuantity { get; private set; }
    public decimal UnitPrice { get; private set; }

    public AddOnCatalog AddOnCatalog { get; private set; } = default!;

    private AddOnPricingTier() { }

    private AddOnPricingTier(
        int id,
        int addOnCatalogId,
        int minQuantity,
        int? maxQuantity,
        decimal unitPrice)
        : base(id)
    {
        AddOnCatalogId = addOnCatalogId;
        MinQuantity = minQuantity;
        MaxQuantity = maxQuantity;
        UnitPrice = unitPrice;
    }

    public static AddOnPricingTier Create(
        int id,
        int addOnCatalogId,
        int minQuantity,
        int? maxQuantity,
        decimal unitPrice)
    {
        return new AddOnPricingTier(id, addOnCatalogId, minQuantity, maxQuantity, unitPrice);
    }
}
