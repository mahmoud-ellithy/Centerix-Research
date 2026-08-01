namespace Centerix.Domain.Platform.Referrals;

using Centerix.Domain.Common.Results;

public static class TenantReferralErrors
{
    public static Error ReferrerTenantIdRequired =>
        Error.Validation("TenantReferral.ReferrerTenantId_Required", "Referrer tenant ID is required");

    public static Error ReferredTenantIdRequired =>
        Error.Validation("TenantReferral.ReferredTenantId_Required", "Referred tenant ID is required");

    public static Error CannotReferSelf =>
        Error.Conflict("TenantReferral.CannotReferSelf", "A tenant cannot refer itself");

    public static Error InvalidRewardValue =>
        Error.Validation("TenantReferral.InvalidRewardValue", "Reward value must be greater than or equal to zero");

    public static Error NotPending =>
        Error.Conflict("TenantReferral.NotPending", "Referral is not in pending status");

    public static Error NotQualified =>
        Error.Conflict("TenantReferral.NotQualified", "Referral is not in qualified status");
}
