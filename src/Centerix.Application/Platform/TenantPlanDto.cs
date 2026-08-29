namespace Centerix.Application.Platform;

public class TenantPlanDto
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public int PlanId { get; set; }
    public string? PlanName { get; set; }

    // Commercial snapshot (frozen at grant time).
    public decimal SnapshotPrice { get; set; }
    public string SnapshotCurrency { get; set; } = default!;
    public int DurationMonths { get; set; }
    public int BonusMonths { get; set; }

    public DateTime StartsAtUtc { get; set; }
    public DateTime BaseEndsAtUtc { get; set; }
    public DateTime EffectiveEndsAtUtc { get; set; }

    // Snapshot limits.
    public int MaxStudents { get; set; }
    public int MaxUsers { get; set; }
    public int MaxBranches { get; set; }
    public int MaxTeachers { get; set; }
    public int StorageGB { get; set; }
    public int SMSQuota { get; set; }

    public bool AutoRenew { get; set; }
    public byte Status { get; set; }
    public string? StatusLabel { get; set; }
}
