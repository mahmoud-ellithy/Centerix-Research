using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
[AllowAnonymous]
public class AuthController(
    UserManager<IdentityUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ITokenService tokenService,
    ILocalizer localizer) : ApiController(localizer)
{
    [HttpPost("login")]
    [EnableRateLimiting("LoginPolicy")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            // Don't reveal that user doesn't exist
            return Unauthorized(new
            {
                error = localizer.Translate("Auth:InvalidCredentials")
            });
        }

        // Check if user is locked out before attempting authentication
        if (await userManager.IsLockedOutAsync(user))
        {
            var lockoutEnd = await userManager.GetLockoutEndDateAsync(user);
            var remainingMinutes = lockoutEnd.HasValue 
                ? Math.Ceiling((lockoutEnd.Value - DateTimeOffset.UtcNow).TotalMinutes) 
                : 0;
            
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = localizer.Translate("Auth:AccountLocked"),
                lockoutRemainingMinutes = remainingMinutes
            });
        }

        // Attempt sign-in with lockout tracking enabled
        var result = await userManager.CheckPasswordAsync(user, request.Password);
        if (!result)
        {
            // Increment failed access count
            await userManager.AccessFailedAsync(user);
            
            // Check if this attempt triggered lockout
            if (await userManager.IsLockedOutAsync(user))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    error = localizer.Translate("Auth:AccountLockedDueToFailedAttempts")
                });
            }

            return Unauthorized(new
            {
                error = localizer.Translate("Auth:InvalidCredentials")
            });
        }

        // Reset failed access count on successful login
        await userManager.ResetAccessFailedCountAsync(user);

        var roles = await userManager.GetRolesAsync(user);

        var permissions = new List<string>();
        foreach (var roleName in roles)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role != null)
            {
                var roleClaims = await roleManager.GetClaimsAsync(role);
                permissions.AddRange(roleClaims
                    .Where(c => c.Type == Permissions.ClaimType)
                    .Select(c => c.Value));
            }
        }

        var token = tokenService.GenerateToken(user, roles, permissions.Distinct().ToList());

        return Ok(new LoginResponse
        {
            Token = token,
            Email = user.Email,
            Roles = roles.ToList()
        });
    }
}

public record LoginRequest(string Email, string Password);

public record LoginResponse
{
    public string Token { get; init; } = string.Empty;
    public string? Email { get; init; }
    public List<string> Roles { get; init; } = [];
}
