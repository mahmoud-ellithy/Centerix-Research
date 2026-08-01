namespace Centerix.Application.Platform.Referrals;

public class TenantReferralCodeDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = default!;
    public int TimesUsed { get; set; }
    public bool IsActive { get; set; }
}
