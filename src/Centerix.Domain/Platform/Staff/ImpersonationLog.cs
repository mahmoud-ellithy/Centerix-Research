namespace Centerix.Domain.Platform.Staff;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Mandatory audit log for support staff impersonation sessions.
/// Append-only — no updates.
/// </summary>
public class ImpersonationLog : Entity
{
    public Guid Id { get; private set; }
    public Guid PlatformUserId { get; private set; }
    public string TenantId { get; private set; } = default!;
    public Guid TargetUserId { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public string Reason { get; private set; } = default!;
    public string IPAddress { get; private set; } = default!;

    public PlatformUser PlatformUser { get; private set; } = default!;

    private ImpersonationLog() { }

    private ImpersonationLog(
        Guid id,
        Guid platformUserId,
        string tenantId,
        Guid targetUserId,
        DateTime startedAt,
        string reason,
        string ipAddress)
    {
        Id = id;
        PlatformUserId = platformUserId;
        TenantId = tenantId;
        TargetUserId = targetUserId;
        StartedAt = startedAt;
        Reason = reason;
        IPAddress = ipAddress;
    }

    public static Result<ImpersonationLog> Create(
        Guid id,
        Guid platformUserId,
        string tenantId,
        Guid targetUserId,
        DateTime startedAt,
        string reason,
        string ipAddress)
    {
        if (platformUserId == Guid.Empty)
            return Error.Validation("ImpersonationLog.UserId_Invalid", "Platform user ID is required");

        if (string.IsNullOrWhiteSpace(tenantId))
            return Error.Validation("ImpersonationLog.TenantId_Required", "Tenant ID is required");

        if (targetUserId == Guid.Empty)
            return Error.Validation("ImpersonationLog.TargetUserId_Invalid", "Target user ID is required");

        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("ImpersonationLog.Reason_Required", "Reason is required");

        if (string.IsNullOrWhiteSpace(ipAddress))
            return Error.Validation("ImpersonationLog.IPAddress_Required", "IP address is required");

        return new ImpersonationLog(id, platformUserId, tenantId, targetUserId, startedAt, reason, ipAddress);
    }

    public Result<Updated> EndSession(DateTime endedAt)
    {
        if (EndedAt.HasValue)
            return Error.Conflict("ImpersonationLog.AlreadyEnded", "Session has already been ended");

        EndedAt = endedAt;
        return Result.Updated;
    }
}
