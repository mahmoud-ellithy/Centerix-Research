using Centerix.Application.Common.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Centerix.Infrastructure.Auth;

public class IdentityService(UserManager<IdentityUser> userManager) : IIdentityService
{
    public async Task<(string UserId, bool Succeeded, IEnumerable<string> Errors)> CreateUserAsync(
        string email, string normalizedEmail, string password)
    {
        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = normalizedEmail,
            NormalizedUserName = normalizedEmail
        };

        var result = await userManager.CreateAsync(user, password);

        return (user.Id, result.Succeeded, result.Errors.Select(e => e.Description));
    }

    public async Task<bool> DeleteUserAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;

        var result = await userManager.DeleteAsync(user);
        return result.Succeeded;
    }

    public async Task<string?> FindUserIdByEmailAsync(string email)
    {
        var user = await userManager.FindByEmailAsync(email);
        return user?.Id;
    }

    public async Task<IList<string>> GetRolesAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return [];

        return await userManager.GetRolesAsync(user);
    }

    public async Task<bool> AddToRoleAsync(string userId, string roleName)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;

        var result = await userManager.AddToRoleAsync(user, roleName);
        return result.Succeeded;
    }

    public async Task<bool> IsInRoleAsync(string userId, string roleName)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;

        return await userManager.IsInRoleAsync(user, roleName);
    }

    public async Task<bool> CheckPasswordAsync(string userId, string password)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;

        return await userManager.CheckPasswordAsync(user, password);
    }
}
