namespace Centerix.Domain.Platform.Tenants;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants.Enums;

/// <summary>
/// Represents an invitation to join a tenant. The invitation may target an existing
/// <see cref="Microsoft.AspNetCore.Identity.IdentityUser"/> (by email) or a person who
/// has not yet registered. The raw token is never stored; only its SHA-256 hash is persisted.
/// </summary>
public class TenantInvitation : Entity
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string NormalizedEmail { get; private set; } = default!;
    public string InvitedByUserId { get; private set; } = default!;
    public string RoleName { get; private set; } = default!;
    public string TokenHash { get; private set; } = default!;
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public InvitationStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? AcceptedAtUtc { get; private set; }
    public string? AcceptedByUserId { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public string? RevokedByUserId { get; private set; }

    private TenantInvitation() { }

    private TenantInvitation(
        Guid id,
        string tenantId,
        string email,
        string normalizedEmail,
        string invitedByUserId,
        string roleName,
        string tokenHash,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        NormalizedEmail = normalizedEmail;
        InvitedByUserId = invitedByUserId;
        RoleName = roleName;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        Status = InvitationStatus.Pending;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public static Result<TenantInvitation> Create(
        Guid id,
        string tenantId,
        string email,
        string invitedByUserId,
        string roleName,
        string tokenHash,
        DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty)
            return Error.Validation("TenantInvitation.Id_Required", "Invitation ID is required");

        if (string.IsNullOrWhiteSpace(tenantId))
            return Error.Validation("TenantInvitation.TenantId_Required", "Tenant ID is required");

        if (string.IsNullOrWhiteSpace(email))
            return Error.Validation("TenantInvitation.Email_Required", "Email is required");

        if (string.IsNullOrWhiteSpace(invitedByUserId))
            return Error.Validation("TenantInvitation.InvitedByUserId_Required", "Invited by user ID is required");

        if (string.IsNullOrWhiteSpace(roleName))
            return Error.Validation("TenantInvitation.RoleName_Required", "Role name is required");

        if (string.IsNullOrWhiteSpace(tokenHash))
            return Error.Validation("TenantInvitation.TokenHash_Required", "Token hash is required");

        if (expiresAtUtc <= DateTimeOffset.UtcNow)
            return Error.Validation("TenantInvitation.ExpiresAtUtc_Invalid", "Expiration must be in the future");

        var normalizedEmail = email.Trim().ToUpperInvariant();

        return new TenantInvitation(
            id, tenantId, email.Trim(), normalizedEmail,
            invitedByUserId, roleName, tokenHash, expiresAtUtc);
    }

    public Result<Updated> Accept(string acceptedByUserId)
    {
        if (Status != InvitationStatus.Pending)
            return Error.Conflict("TenantInvitation.InvalidStatus", $"Cannot accept invitation in {Status} status");

        if (ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            Status = InvitationStatus.Expired;
            return Error.Conflict("TenantInvitation.Expired", "This invitation has expired");
        }

        if (string.IsNullOrWhiteSpace(acceptedByUserId))
            return Error.Validation("TenantInvitation.AcceptedByUserId_Required", "Accepted by user ID is required");

        Status = InvitationStatus.Accepted;
        AcceptedAtUtc = DateTimeOffset.UtcNow;
        AcceptedByUserId = acceptedByUserId;

        return Result.Updated;
    }

    public Result<Updated> Revoke(string revokedByUserId)
    {
        if (Status != InvitationStatus.Pending)
            return Error.Conflict("TenantInvitation.InvalidStatus", $"Cannot revoke invitation in {Status} status");

        if (string.IsNullOrWhiteSpace(revokedByUserId))
            return Error.Validation("TenantInvitation.RevokedByUserId_Required", "Revoked by user ID is required");

        Status = InvitationStatus.Revoked;
        RevokedAtUtc = DateTimeOffset.UtcNow;
        RevokedByUserId = revokedByUserId;

        return Result.Updated;
    }

    public Result<Updated> MarkExpired()
    {
        if (Status != InvitationStatus.Pending)
            return Result.Updated;

        Status = InvitationStatus.Expired;
        return Result.Updated;
    }
}
