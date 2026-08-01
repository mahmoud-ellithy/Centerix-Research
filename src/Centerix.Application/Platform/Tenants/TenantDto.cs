namespace Centerix.Application.Platform.Tenants;

public class TenantDto
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = default!;
    public string Subdomain { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string Country { get; set; } = default!;
    public string Currency { get; set; } = default!;
    public string Timezone { get; set; } = default!;
    public string OwnerFirstName { get; set; } = default!;
    public string OwnerLastName { get; set; } = default!;
    public string OwnerEmail { get; set; } = default!;
    public string? OwnerPhone { get; set; }
    public byte IsolationMode { get; set; }
    public byte LifecycleStatus { get; set; }
    public bool IsActive { get; set; }
    public int? CurrentPlanId { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public DateTime? ValidUpTo { get; set; }
}
