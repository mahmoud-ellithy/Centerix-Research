using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
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
    }
}

public interface ITokenService
{
    string GenerateToken(IdentityUser user, IList<string> roles, IList<string> permissions);
}

public class JwtTokenService(IOptions<JwtSettings> jwtSettings) : ITokenService
{
    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    public string GenerateToken(IdentityUser user, IList<string> roles, IList<string> permissions)
    {
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

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationInMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
