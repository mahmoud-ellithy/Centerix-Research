namespace Centerix.Infrastructure.Auditing;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Auditing;
using Centerix.Infrastructure.Data;
using Microsoft.Extensions.Logging;

/// <summary>
/// Writes <see cref="AuditLog"/> rows. Failures are logged and swallowed so a broken
/// audit trail never fails the business operation that triggered it.
/// </summary>
public class AuditWriter(
    AppDbContext dbContext,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<AuditWriter> logger) : IAuditWriter
{
    public async Task WriteAsync(
        string action,
        string? entityType = null,
        string? entityId = null,
        string? oldValue = null,
        string? newValue = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = currentUser.IsAuthenticated && !string.IsNullOrEmpty(currentUser.UserId)
                ? currentUser.UserId
                : null;

            var entry = AuditLog.Create(
                id: 0,
                action: action,
                entityType: entityType,
                entityId: entityId,
                userId: userId,
                ipAddress: null, // populated by middleware/handler if available
                oldValue: oldValue,
                newValue: newValue,
                performedAt: timeProvider.GetUtcNow().UtcDateTime);

            if (!entry.IsSuccess)
            {
                logger.LogWarning("Audit write skipped: {Errors}", string.Join(", ", entry.Errors!.Select(e => e.Code)));
                return;
            }

            dbContext.AuditLogs.Add(entry.Value);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Audit must never break the calling operation.
            logger.LogError(ex, "Failed to write audit entry for action {Action} on {EntityType}#{EntityId}", action, entityType, entityId);
        }
    }
}

