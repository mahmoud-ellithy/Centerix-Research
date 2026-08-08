namespace Centerix.Domain.Platform.Referrals;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Referrals.Enums;
using Centerix.Domain.Platform.Referrals.Events;

public class TenantReferral : AuditableEntity<Guid>
{
    public string ReferrerTenantId { get; private set; } = default!;
    public string ReferredTenantId { get; private set; } = default!;
    public Guid ReferralCodeId { get; private set; }
    public ReferralStatus Status { get; private set; }
    public DateTime? QualifiedAt { get; private set; }
    public ReferralRewardType RewardType { get; private set; }
    public decimal RewardValue { get; private set; }
    public string? RewardAppliedTo { get; private set; }
    public DateTime? RewardAppliedAt { get; private set; }
    public DateTime? LockedUntil { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }
    public Guid? RevokedBy { get; private set; }

    public TenantReferralCode TenantReferralCode { get; private set; } = default!;

    private TenantReferral() { }

    private TenantReferral(
        Guid id,
        string referrerTenantId,
        string referredTenantId,
        Guid referralCodeId,
        ReferralStatus status,
        ReferralRewardType rewardType,
        decimal rewardValue)
        : base(id)
    {
        ReferrerTenantId = referrerTenantId;
        ReferredTenantId = referredTenantId;
        ReferralCodeId = referralCodeId;
        Status = status;
        RewardType = rewardType;
        RewardValue = rewardValue;
    }

    public static Result<TenantReferral> Create(
        Guid id,
        string referrerTenantId,
        string referredTenantId,
        Guid referralCodeId,
        ReferralRewardType rewardType,
        decimal rewardValue)
    {
        if (string.IsNullOrWhiteSpace(referrerTenantId))
            return TenantReferralErrors.ReferrerTenantIdRequired;

        if (string.IsNullOrWhiteSpace(referredTenantId))
            return TenantReferralErrors.ReferredTenantIdRequired;

        if (referrerTenantId == referredTenantId)
            return TenantReferralErrors.CannotReferSelf;

        if (rewardValue < 0)
            return TenantReferralErrors.InvalidRewardValue;

        if (!Enum.IsDefined(rewardType))
            return Error.Validation("TenantReferral.RewardType_Invalid", "Invalid referral reward type");

        return new TenantReferral(
            id,
            referrerTenantId,
            referredTenantId,
            referralCodeId,
            ReferralStatus.Pending,
            rewardType,
            rewardValue);
    }

    public Result<Updated> Qualify()
    {
        if (Status != ReferralStatus.Pending)
            return TenantReferralErrors.NotPending;

        Status = ReferralStatus.Qualified;
        QualifiedAt = DateTime.UtcNow;
        LockedUntil = DateTime.UtcNow.AddDays(90);

        AddDomainEvent(new ReferralQualifiedEvent(Id, ReferrerTenantId, ReferredTenantId));

        return Result.Updated;
    }

    public Result<Updated> ApplyReward(string appliedTo)
    {
        if (Status != ReferralStatus.Qualified)
            return TenantReferralErrors.NotQualified;

        if (LockedUntil.HasValue && DateTime.UtcNow < LockedUntil.Value)
            return TenantReferralErrors.LockedPeriod;

        Status = ReferralStatus.RewardApplied;
        RewardAppliedTo = appliedTo;
        RewardAppliedAt = DateTime.UtcNow;

        AddDomainEvent(new ReferralRewardAppliedEvent(Id, ReferrerTenantId, ReferredTenantId, RewardType, RewardValue));

        return Result.Updated;
    }

    public Result<Updated> Revoke(string reason, Guid revokedBy)
    {
        if (Status is ReferralStatus.Expired or ReferralStatus.Revoked)
            return TenantReferralErrors.CannotRevoke;

        Status = ReferralStatus.Revoked;
        RevokedAt = DateTime.UtcNow;
        RevokedReason = reason;
        RevokedBy = revokedBy;

        return Result.Updated;
    }
}
