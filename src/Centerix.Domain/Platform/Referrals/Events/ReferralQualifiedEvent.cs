namespace Centerix.Domain.Platform.Referrals.Events;

using Centerix.Domain.Common;

public sealed class ReferralQualifiedEvent(
    Guid referralId,
    string referrerTenantId,
    string referredTenantId) : DomainEvent
{
    public Guid ReferralId { get; } = referralId;
    public string ReferrerTenantId { get; } = referrerTenantId;
    public string ReferredTenantId { get; } = referredTenantId;
}
