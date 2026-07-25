namespace Centerix.Infrastructure.Auth;

using Microsoft.AspNetCore.Identity;

public class ApplicationRole : IdentityRole
{
    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }

    public string? Code { get; set; }
    public string? DisplayName { get; set; }
    public bool IsSystem { get; set; }
}
