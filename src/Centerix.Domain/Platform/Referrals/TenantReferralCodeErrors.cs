namespace Centerix.Domain.Platform.Referrals;

using Centerix.Domain.Common.Results;

public static class TenantReferralCodeErrors
{
    public static Error CodeRequired =>
        Error.Validation("TenantReferralCode.Code_Required", "Referral code is required");
}
