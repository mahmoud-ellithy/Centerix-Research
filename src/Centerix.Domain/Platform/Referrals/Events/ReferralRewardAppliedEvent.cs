namespace Centerix.Domain.Platform.Referrals.Events;

using Centerix.Domain.Common;
using Centerix.Domain.Platform.Referrals.Enums;

public sealed class ReferralRewardAppliedEvent(
    Guid referralId,
    string referrerTenantId,
    string referredTenantId,
    ReferralRewardType rewardType,
    decimal rewardValue) : DomainEvent
{
    public Guid ReferralId { get; } = referralId;
    public string ReferrerTenantId { get; } = referrerTenantId;
    public string ReferredTenantId { get; } = referredTenantId;
    public ReferralRewardType RewardType { get; } = rewardType;
    public decimal RewardValue { get; } = rewardValue;
}
