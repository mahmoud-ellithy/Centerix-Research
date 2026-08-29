namespace Centerix.Application.Common.Interfaces;

using Centerix.Domain.Platform.Subscriptions.Enums;

/// <summary>
/// Effective commercial state of one tenant's subscription as of NOW.
/// Resolution is lazy: expiration is computed from the subscription's EffectiveEndsAtUtc
/// against the current instant — never from a background job having already flipped the
/// persisted status. Implementations MAY write back the persisted Expired status when they
/// detect it (write-through), but callers must not rely on that having happened.
/// </summary>
public interface ISubscriptionStateService
{
    Task<SubscriptionStateInfo> GetCurrentAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Resolved subscription state for a tenant.</summary>
/// <param name="SubscriptionId">Null when the tenant holds no subscription row.</param>
/// <param name="PersistedStatus">Status as stored (may lag reality until lazily synced); null when none.</param>
/// <param name="EffectiveEndsAtUtc">Authoritative commercial end date (base term + bonus).</param>
/// <param name="IsActiveAsOfNow">True iff an ACTIVE subscription whose effective end is in the future.</param>
public sealed record SubscriptionStateInfo(
    Guid? SubscriptionId,
    SubscriptionStatus? PersistedStatus,
    DateTime? EffectiveEndsAtUtc,
    bool IsActiveAsOfNow);
