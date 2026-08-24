namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Abstraction over ASP.NET Core RoleManager operations.
/// </summary>
public interface IRoleService
{
    Task<bool> ExistsAsync(string roleName);
}
