using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class AuthController(
    UserManager<IdentityUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IAppDbContext dbContext,
    ITokenService tokenService,
    IRefreshTokenService refreshTokenService,
    ILocalizer localizer) : ApiController(localizer)
{
    [HttpPost("login")]
    [AllowAnonymous]
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

        // Resolve permission codes from the RolePermission table joined to the user's roles.
        var permissions = await ResolvePermissionsForRolesAsync(roles);

        var accessToken = tokenService.GenerateAccessToken(user, roles, permissions);
        var refreshToken = await refreshTokenService.IssueAsync(
            userId: user.Id,
            deviceInfo: Request.Headers.UserAgent.ToString(),
            ipAddress: Request.HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            Email = user.Email,
            Roles = roles.ToList()
        });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        var result = await refreshTokenService.RotateAsync(
            refreshToken: request.RefreshToken,
            deviceInfo: Request.Headers.UserAgent.ToString(),
            ipAddress: Request.HttpContext.Connection.RemoteIpAddress?.ToString());

        if (!result.IsSuccess)
        {
            return Unauthorized(new { error = localizer.Translate("Auth:InvalidRefreshToken") });
        }

        return Ok(new RefreshResponse
        {
            AccessToken = result.Value.AccessToken,
            RefreshToken = result.Value.RefreshToken
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(LogoutRequest request)
    {
        await refreshTokenService.RevokeAsync(request.RefreshToken);
        return NoContent();
    }

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await refreshTokenService.RevokeAllAsync(userId);
        return NoContent();
    }

    private async Task<List<string>> ResolvePermissionsForRolesAsync(IList<string> roles)
    {
        if (roles.Count == 0)
        {
            return [];
        }

        var roleIds = new List<string>();
        foreach (var name in roles)
        {
            var role = await roleManager.FindByNameAsync(name);
            if (role != null)
            {
                roleIds.Add(role.Id);
            }
        }

        if (roleIds.Count == 0)
        {
            return [];
        }

        var permissionCodes = await (
            from rp in dbContext.RolePermissions.AsNoTracking()
            join p in dbContext.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
            where roleIds.Contains(rp.RoleId)
            select p.Code).Distinct().ToListAsync();

        return permissionCodes;
    }
}

public record LoginRequest(string Email, string Password);

public record LoginResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public string? Email { get; init; }
    public List<string> Roles { get; init; } = [];
}

public record RefreshRequest(string RefreshToken);

public record RefreshResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
}

public record LogoutRequest(string RefreshToken);
