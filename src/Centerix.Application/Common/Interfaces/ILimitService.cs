namespace Centerix.Application.Common.Interfaces;

using Centerix.Domain.Common.Results;

/// <summary>
/// Reusable plan-limit enforcement for any business module ("how many X may this tenant have?").
///
/// Precedence (approved): TenantLimitOverride (platform-granted, replaces) → subscription
/// SNAPSHOT limit → deny when undefined. Overrides never read live Plan values.
///
/// Concurrency: capacity reservation is performed with an ATOMIC conditional update against the
/// tenant's usage counter inside the caller's ambient transaction, so two concurrent creators can
/// never both pass a full quota (the loser observes 0 affected rows and is denied).
/// </summary>
public interface ILimitService
{
    /// <summary>
    /// Resolves the effective maximum for <paramref name="limitType"/>:
    /// override first, then the active subscription's snapshot limit.
    /// Returns null when no active subscription defines the limit.
    /// </summary>
    Task<int?> GetEffectiveMaxAsync(string tenantId, string limitType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves one unit of capacity. Returns Failure when the tenant has no active
    /// subscription, the limit is not defined, or the quota is exhausted / usage tracking is not
    /// provisioned. Must be called INSIDE the same transaction/unit-of-work that persists the
    /// business record so rollback releases the reserved slot.
    /// </summary>
    Task<Result<Updated>> ReserveAsync(string tenantId, string limitType, CancellationToken cancellationToken = default);

    /// <summary>Releases one unit of previously reserved capacity (never below zero).</summary>
    Task ReleaseAsync(string tenantId, string limitType, CancellationToken cancellationToken = default);
}

/// <summary>Canonical limit types shared across modules. Delegates to domain codes.</summary>
public static class LimitTypes
{
    public const string Students = Centerix.Domain.Platform.Subscriptions.LimitTypeCodes.Students;
    public const string Users = Centerix.Domain.Platform.Subscriptions.LimitTypeCodes.Users;
    public const string Branches = Centerix.Domain.Platform.Subscriptions.LimitTypeCodes.Branches;
    public const string Teachers = Centerix.Domain.Platform.Subscriptions.LimitTypeCodes.Teachers;

    public static IReadOnlyCollection<string> All => Centerix.Domain.Platform.Subscriptions.LimitTypeCodes.All;
}
