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
    }
}

public interface ITokenService
{
    string GenerateAccessToken(IdentityUser user, IList<string> roles, IList<string> permissions);
    (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken();
}

public class JwtTokenService(IOptions<JwtSettings> jwtSettings) : ITokenService
{
    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    public string GenerateAccessToken(IdentityUser user, IList<string> roles, IList<string> permissions)
    {
        // Intentionally TENANT-AGNOSTIC: no tenant claim is emitted. The active tenant is a client
        // selection (header/host) verified server-side on every request via TenantMembership in
        // TenantGuardMiddleware. A JWT tenant claim must never be introduced as a source of truth or
        // as proof of membership — multi-tenancy is authorized per request, not pinned in the token.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        foreach (var permission in permissions)
            claims.Add(new Claim(Permissions.ClaimType, permission));

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


