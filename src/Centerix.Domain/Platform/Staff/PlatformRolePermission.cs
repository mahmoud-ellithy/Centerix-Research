namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Junction: PlatformRole ↔ PlatformPermission.
/// </summary>
public class PlatformRolePermission : Entity
{
    public int RoleId { get; private set; }
    public int PermissionId { get; private set; }

    public PlatformRole Role { get; private set; } = default!;
    public PlatformPermission Permission { get; private set; } = default!;

    private PlatformRolePermission() { }

    private PlatformRolePermission(int roleId, int permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public static Result<PlatformRolePermission> Create(int roleId, int permissionId)
    {
        if (roleId <= 0)
            return Error.Validation("PlatformRolePermission.RoleId_Invalid", "Role ID must be greater than zero");

        if (permissionId <= 0)
            return Error.Validation("PlatformRolePermission.PermissionId_Invalid", "Permission ID must be greater than zero");

        return new PlatformRolePermission(roleId, permissionId);
    }
}
