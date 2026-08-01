namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common.Results;

public static class PlatformRoleErrors
{
    public static Error CodeRequired =>
        Error.Validation("PlatformRole.Code_Required", "Role code is required");

    public static Error DisplayNameRequired =>
        Error.Validation("PlatformRole.DisplayName_Required", "Display name is required");
}
