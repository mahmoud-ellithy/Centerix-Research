namespace Centerix.Domain.Platform.Authorization;

using Centerix.Domain.Common.Results;

public static class PermissionErrors
{
    public static Error ModuleRequired =>
        Error.Validation("Permission.Module_Required", "Permission module is required");

    public static Error ActionRequired =>
        Error.Validation("Permission.Action_Required", "Permission action is required");

    public static Error CodeRequired =>
        Error.Validation("Permission.Code_Required", "Permission code is required");

    public static Error CodeFormatInvalid =>
        Error.Validation("Permission.Code_FormatInvalid", "Permission code must follow 'Module.Action' format");
}
