namespace Centerix.Domain.Platform.Authorization;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class Permission : GlobalAuditableEntity<int>
{
    public string Module { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Code { get; private set; } = default!;
    public string? Description { get; private set; }

    private readonly List<RolePermission> _rolePermissions = [];
    public IReadOnlyList<RolePermission> RolePermissions => _rolePermissions.AsReadOnly();

    private Permission() { }

    private Permission(int id, string module, string action, string code, string? description)
        : base(id)
    {
        Module = module;
        Action = action;
        Code = code;
        Description = description;
    }

    public static Result<Permission> Create(int id, string module, string action, string code, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(module))
            return PermissionErrors.ModuleRequired;

        if (string.IsNullOrWhiteSpace(action))
            return PermissionErrors.ActionRequired;

        if (string.IsNullOrWhiteSpace(code))
            return PermissionErrors.CodeRequired;

        var normalizedCode = code.Trim();
        if (!string.Equals(normalizedCode, $"{module.Trim()}.{action.Trim()}", StringComparison.Ordinal))
            return PermissionErrors.CodeFormatInvalid;

        return new Permission(id, module.Trim(), action.Trim(), normalizedCode, description?.Trim());
    }
}
