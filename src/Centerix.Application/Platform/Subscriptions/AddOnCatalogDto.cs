namespace Centerix.Application.Platform.Subscriptions;

public class AddOnCatalogDto
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string UnitType { get; set; } = default!;
    public int UnitQuantity { get; set; }
    public byte BillingType { get; set; }
    public bool IsActive { get; set; }
}
