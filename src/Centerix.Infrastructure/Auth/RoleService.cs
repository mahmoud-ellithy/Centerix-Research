using Centerix.Application.Common.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Centerix.Infrastructure.Auth;

public class RoleService(RoleManager<ApplicationRole> roleManager) : IRoleService
{
    public async Task<bool> ExistsAsync(string roleName)
    {
        return await roleManager.RoleExistsAsync(roleName);
    }
}
