namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common.Results;

public static class PlatformUserErrors
{
    public static Error EmailRequired =>
        Error.Validation("PlatformUser.Email_Required", "Email is required");

    public static Error FullNameRequired =>
        Error.Validation("PlatformUser.FullName_Required", "Full name is required");

    public static Error PasswordHashRequired =>
        Error.Validation("PlatformUser.PasswordHash_Required", "Password hash is required");

    public static Error AlreadyDeactivated =>
        Error.Conflict("PlatformUser.AlreadyDeactivated", "Platform user is already deactivated");

    public static Error AlreadyActive =>
        Error.Conflict("PlatformUser.AlreadyActive", "Platform user is already active");
}
