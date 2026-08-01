namespace Centerix.Application.Platform.Referrals;

public class TenantReferralDto
{
    public Guid Id { get; set; }
    public string ReferrerTenantId { get; set; } = default!;
    public string ReferredTenantId { get; set; } = default!;
    public byte Status { get; set; }
    public byte RewardType { get; set; }
    public decimal RewardValue { get; set; }
    public DateTime? QualifiedAt { get; set; }
    public DateTime? RewardAppliedAt { get; set; }
}
