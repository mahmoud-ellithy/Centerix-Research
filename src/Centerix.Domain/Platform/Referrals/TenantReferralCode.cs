namespace Centerix.Domain.Platform.Referrals;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class TenantReferralCode : AuditableEntity<Guid>
{
    public string Code { get; private set; } = default!;
    public int TimesUsed { get; private set; }
    public bool IsActive { get; private set; }

    private TenantReferralCode() { }

    private TenantReferralCode(
        Guid id,
        string code,
        int timesUsed,
        bool isActive)
        : base(id)
    {
        Code = code;
        TimesUsed = timesUsed;
        IsActive = isActive;
    }

    public static Result<TenantReferralCode> Create(
        Guid id,
        string code,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(code))
            return TenantReferralCodeErrors.CodeRequired;

        return new TenantReferralCode(id, code, 0, isActive);
    }

    public Result<Updated> IncrementUsage()
    {
        TimesUsed++;
        return Result.Updated;
    }
}
