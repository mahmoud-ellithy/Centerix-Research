namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Junction: PlatformUser ↔ PlatformRole.
/// </summary>
public class PlatformUserRole : Entity
{
    public Guid PlatformUserId { get; private set; }
    public int RoleId { get; private set; }

    public PlatformUser PlatformUser { get; private set; } = default!;
    public PlatformRole Role { get; private set; } = default!;

    private PlatformUserRole() { }

    private PlatformUserRole(Guid platformUserId, int roleId)
    {
        PlatformUserId = platformUserId;
        RoleId = roleId;
    }

    public static Result<PlatformUserRole> Create(Guid platformUserId, int roleId)
    {
        if (platformUserId == Guid.Empty)
            return Error.Validation("PlatformUserRole.UserId_Invalid", "Platform user ID is required");

        if (roleId <= 0)
            return Error.Validation("PlatformUserRole.RoleId_Invalid", "Role ID must be greater than zero");

        return new PlatformUserRole(platformUserId, roleId);
    }
}
