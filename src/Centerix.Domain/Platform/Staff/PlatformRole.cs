namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Platform-specific roles (SuperAdmin, SalesRep, SupportAgent, BillingManager).
/// Completely separate from tenant Roles.
/// </summary>
public class PlatformRole : Entity
{
    public int Id { get; private set; }
    public string Code { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;

    private readonly List<PlatformUserRole> _userRoles = [];
    public IReadOnlyList<PlatformUserRole> UserRoles => _userRoles.AsReadOnly();

    private readonly List<PlatformRolePermission> _rolePermissions = [];
    public IReadOnlyList<PlatformRolePermission> RolePermissions => _rolePermissions.AsReadOnly();

    private PlatformRole() { }

    private PlatformRole(int id, string code, string displayName)
    {
        Id = id;
        Code = code;
        DisplayName = displayName;
    }

    public static Result<PlatformRole> Create(int id, string code, string displayName)
    {
        if (string.IsNullOrWhiteSpace(code))
            return PlatformRoleErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return PlatformRoleErrors.DisplayNameRequired;

        return new PlatformRole(id, code.Trim(), displayName.Trim());
    }
}
