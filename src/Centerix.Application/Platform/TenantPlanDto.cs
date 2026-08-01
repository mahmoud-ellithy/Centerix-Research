namespace Centerix.Application.Platform;

public class TenantPlanDto
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public int PlanId { get; set; }
    public decimal SnapshotPrice { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool AutoRenew { get; set; }
    public byte Status { get; set; }
    public string? StatusLabel { get; set; }
    public PlanDto? Plan { get; set; }
}
