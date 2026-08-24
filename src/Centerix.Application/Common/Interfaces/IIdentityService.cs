namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Abstraction over ASP.NET Core Identity operations, keeping the Application layer
/// free from direct references to Microsoft.AspNetCore.Identity.
/// </summary>
public interface IIdentityService
{
    Task<(string UserId, bool Succeeded, IEnumerable<string> Errors)> CreateUserAsync(
        string email, string normalizedEmail, string password);

    Task<bool> DeleteUserAsync(string userId);

    Task<string?> FindUserIdByEmailAsync(string email);

    Task<IList<string>> GetRolesAsync(string userId);

    Task<bool> AddToRoleAsync(string userId, string roleName);

    Task<bool> IsInRoleAsync(string userId, string roleName);

    Task<bool> CheckPasswordAsync(string userId, string password);
}
