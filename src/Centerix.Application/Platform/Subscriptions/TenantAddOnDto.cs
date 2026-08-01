namespace Centerix.Application.Platform.Subscriptions;

public class TenantAddOnDto
{
    public Guid Id { get; set; }
    public int AddOnCatalogId { get; set; }
    public int Quantity { get; set; }
    public decimal SnapshotUnitPrice { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public byte Status { get; set; }
}
