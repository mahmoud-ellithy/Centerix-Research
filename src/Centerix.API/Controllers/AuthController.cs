using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user == null || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Unauthorized(new
            {
                error = localizer.Translate("Auth:InvalidCredentials")
            });
        }

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
