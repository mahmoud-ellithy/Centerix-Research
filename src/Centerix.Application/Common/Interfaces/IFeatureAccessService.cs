namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Answers "does this TENANT'S subscription include this product capability?".
/// Deliberately separate from permissions ("may this USER perform this operation?").
/// Neither features nor permissions are carried in JWTs; resolution always happens
/// server-side against the tenant's CURRENT subscription entitlement snapshot.
/// </summary>
public interface IFeatureAccessService
{
    /// <summary>
    /// True iff the tenant has an ACTIVE (unexpired, not suspended/cancelled) subscription whose
    /// entitlement snapshot contains <paramref name="featureCode"/>. Platform admins are NOT
    /// special-cased here: feature checks are tenant-scoped by definition.
    /// </summary>
    Task<bool> HasFeatureAsync(string tenantId, string featureCode, CancellationToken cancellationToken = default);
}
