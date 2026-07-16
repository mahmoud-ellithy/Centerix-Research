using Finbuckle.MultiTenant.Abstractions;

namespace Centerix.Infrastructure.Tenancy;

public class CenterixTenantInfo : ITenantInfo
{
    public string Id { get; set; } = default!;
    public string Identifier { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ConnectionString { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime ValidUpTo { get; set; }
    public bool IsActive { get; set; }

    // Missing fields from ERD
    public string? Slug { get; set; }
    public string? Subdomain { get; set; }
    public string? DisplayName { get; set; }
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? Country { get; set; }
    public string? Currency { get; set; }
    public string? Timezone { get; set; }
    public byte Status { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public DateTime CreatedAt { get; set; }
}