using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Centerix.Application.Common.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Centerix.Infrastructure.Auth;

public class JwtSettings
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpirationInMinutes { get; set; } = 60;
    public int RefreshExpirationInDays { get; set; } = 7;

    /// <summary>
    /// AUTH-002 refresh-race grace, in seconds. When a refresh token has just been rotated,
    /// a presentation of that SAME token is treated as a concurrent/late retry of the rotation
    /// (HTTP 409 conflict, no credentials issued, chain untouched) for at most this long after
    /// the rotation was committed. Outside the window the presentation is confirmed reuse and
    /// the normal reuse policy applies.
    /// <para>
    /// Server-controlled and hard-bounded in <see cref="Validate"/>. The window NEVER mints a
    /// token pair, so widening it cannot become an authentication path - it only decides whether
    /// a presentation is answered with a conflict or with family revocation. 0 disables the
    /// grace entirely (every presentation of a rotated token is reuse).
    /// </para>
    /// </summary>
    public int RefreshRotationGraceSeconds { get; set; } = 5;

    /// <summary>
    /// Validates that required JWT settings are properly configured.
    /// Should be called at application startup.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Secret))
            throw new InvalidOperationException("JWT Secret is not configured. Set 'JwtSettings:Secret' in environment variables or User Secrets.");

        if (Secret.Length < 32)
            throw new InvalidOperationException("JWT Secret must be at least 32 characters for security.");

        if (string.IsNullOrWhiteSpace(Issuer))
            throw new InvalidOperationException("JWT Issuer is not configured. Set 'JwtSettings:Issuer'.");

        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("JWT Audience is not configured. Set 'JwtSettings:Audience'.");

        if (RefreshExpirationInDays < 1)
            throw new InvalidOperationException("JwtSettings:RefreshExpirationInDays must be at least 1 day.");

        if (RefreshRotationGraceSeconds < 0 || RefreshRotationGraceSeconds > MaxRefreshRotationGraceSeconds)
            throw new InvalidOperationException(
                $"JwtSettings:RefreshRotationGraceSeconds must be between 0 and {MaxRefreshRotationGraceSeconds} seconds.");
    }

    /// <summary>Upper bound for <see cref="RefreshRotationGraceSeconds"/>.</summary>
    public const int MaxRefreshRotationGraceSeconds = 300;
}

public interface ITokenService
{
    string GenerateAccessToken(IdentityUser user, IList<string> roles);
    (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken();
}

public class JwtTokenService(IOptions<JwtSettings> jwtSettings) : ITokenService
{
    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    public string GenerateAccessToken(IdentityUser user, IList<string> roles)
    {
        // TENANT-AGNOSTIC & PERMISSION-FREE: The JWT contains only identity claims and
        // global roles. Tenant-specific permissions are resolved per-request via
        // TenantPermissionResolver from: Membership → Role → RolePermission → Permission.
        // This ensures permissions cannot leak between tenants and removes the need to
        // invalidate/reissue tokens when tenant-scoped permissions change.
        // T22: The PlatformAdmin role claim embedded here is a HINT, not an authority — every
        // PlatformAdmin decision is re-validated against the identity store per request by
        // IPlatformAdminVerifier, so revocation/lockout takes effect before token expiry.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationInMinutes);

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken()
    {
        // 256 bits of entropy, base64url-encoded (no '=' padding).
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshExpirationInDays);
        return (token, expiresAt);
    }
}


