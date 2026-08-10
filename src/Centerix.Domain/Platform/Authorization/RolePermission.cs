namespace Centerix.Domain.Platform.Authorization;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class RolePermission : Entity
{
    public string RoleId { get; private set; } = default!;
    public int PermissionId { get; private set; }

    public Permission Permission { get; private set; } = default!;

    private RolePermission() { }

    private RolePermission(string roleId, int permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public static Result<RolePermission> Create(string roleId, int permissionId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            return Error.Validation("RolePermission.RoleId_Required", "Role ID is required");

        if (permissionId <= 0)
            return Error.Validation("RolePermission.PermissionId_Invalid", "Permission ID must be greater than zero");

        return new RolePermission(roleId, permissionId);
    }
}
