namespace Centerix.Application.Platform.Subscriptions;

public class TenantLimitOverrideDto
{
    public Guid Id { get; set; }
    public string LimitType { get; set; } = default!;
    public int OverrideValue { get; set; }
    public string? Reason { get; set; }
}
