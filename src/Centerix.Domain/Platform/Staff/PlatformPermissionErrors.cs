namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common.Results;

public static class PlatformPermissionErrors
{
    public static Error ModuleRequired =>
        Error.Validation("PlatformPermission.Module_Required", "Module is required");

    public static Error ActionRequired =>
        Error.Validation("PlatformPermission.Action_Required", "Action is required");

    public static Error CodeRequired =>
        Error.Validation("PlatformPermission.Code_Required", "Code is required");
}
