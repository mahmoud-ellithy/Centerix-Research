namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Platform-specific permissions (Module, Action, Code).
/// Completely separate from tenant Permissions catalog.
/// </summary>
public class PlatformPermission : Entity
{
    public int Id { get; private set; }
    public string Module { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Code { get; private set; } = default!;

    private readonly List<PlatformRolePermission> _rolePermissions = [];
    public IReadOnlyList<PlatformRolePermission> RolePermissions => _rolePermissions.AsReadOnly();

    private PlatformPermission() { }

    private PlatformPermission(int id, string module, string action, string code)
    {
        Id = id;
        Module = module;
        Action = action;
        Code = code;
    }

    public static Result<PlatformPermission> Create(int id, string module, string action, string code)
    {
        if (string.IsNullOrWhiteSpace(module))
            return PlatformPermissionErrors.ModuleRequired;

        if (string.IsNullOrWhiteSpace(action))
            return PlatformPermissionErrors.ActionRequired;

        if (string.IsNullOrWhiteSpace(code))
            return PlatformPermissionErrors.CodeRequired;

        return new PlatformPermission(id, module.Trim(), action.Trim(), code.Trim());
    }
}
