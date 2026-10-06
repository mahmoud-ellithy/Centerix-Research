namespace Centerix.Domain.Authentication;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Issued refresh token for an <c>IdentityUser</c>. Rotated on every use: a refresh
/// consumes the current token and mints a new pair (access + refresh). Revocation
/// can be global (revoke all tokens for a user) or per-token.
/// <para>
/// AUTH-001: refresh tokens are authentication artifacts, not tenant-owned rows. They are
/// created before any tenant context exists (anonymous login), are resolved by SHA-256 hash
/// uniqueness plus the owning <see cref="UserId"/>, and are revoked per user - never per
/// tenant. Deriving from <see cref="GlobalAuditableEntity{TId}"/> keeps them outside the
/// global tenant query filter and the <c>TenantInterceptor</c> stamping path, so issuance
/// can no longer fail with a NOT NULL violation when no tenant is resolved.
/// </para>
/// </summary>
public class RefreshToken : GlobalAuditableEntity<Guid>
{
    public string UserId { get; private set; } = default!;
    public string TokenHash { get; private set; } = default!;
    public string? DeviceInfo { get; private set; }
    public string? IPAddress { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
    public bool IsRevoked => RevokedAtUtc.HasValue;
    public bool IsActive => !IsRevoked && !IsExpired;

    private RefreshToken() { }

    private RefreshToken(
        Guid id,
        string userId,
        string tokenHash,
        DateTime expiresAtUtc,
        string? deviceInfo,
        string? ipAddress)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        DeviceInfo = deviceInfo;
        IPAddress = ipAddress;
    }

    public static Result<RefreshToken> Create(
        Guid id,
        string userId,
        string tokenHash,
        DateTime expiresAtUtc,
        string? deviceInfo = null,
        string? ipAddress = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return RefreshTokenErrors.UserIdRequired;

        if (string.IsNullOrWhiteSpace(tokenHash))
            return RefreshTokenErrors.TokenHashRequired;

        if (expiresAtUtc <= DateTime.UtcNow)
            return RefreshTokenErrors.ExpiryInPast;

        return new RefreshToken(id, userId, tokenHash, expiresAtUtc, deviceInfo, ipAddress);
    }

    public Result<Updated> Revoke()
    {
        if (IsRevoked)
            return RefreshTokenErrors.AlreadyRevoked;

        RevokedAtUtc = DateTime.UtcNow;
        return Result.Updated;
    }

    /// <summary>Mark this token as superseded by the rotated replacement.</summary>
    public Result<Updated> ReplaceWith(string newTokenHash)
    {
        if (string.IsNullOrWhiteSpace(newTokenHash))
            return RefreshTokenErrors.TokenHashRequired;

        if (IsRevoked)
            return RefreshTokenErrors.AlreadyRevoked;

        ReplacedByTokenHash = newTokenHash;
        RevokedAtUtc = DateTime.UtcNow;
        return Result.Updated;
    }
}
