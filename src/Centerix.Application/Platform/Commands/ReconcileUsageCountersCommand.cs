namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// REV-011 — PLATFORM-ONLY: recomputes a tenant's usage counters from the real rows and
/// writes the counter row in one update. Repairs drift from failed creates, missed releases
/// or any other accounting leak (counters are otherwise only moved by Reserve/Release).
/// Counts use <c>IgnoreQueryFilters()</c> with an EXPLICIT <c>TenantId == target</c>
/// predicate (soft-deleted students/teachers/branches excluded; only Active memberships
/// count as users) so the reconciliation is exact regardless of the caller's context.
/// </summary>
public record ReconcileUsageCountersCommand(Guid TenantId) : IRequest<Result<Updated>>;

public class ReconcileUsageCountersHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter) : IRequestHandler<ReconcileUsageCountersCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        ReconcileUsageCountersCommand request,
        CancellationToken cancellationToken)
    {
        var guardResult = await platformAdminGuard.EnsurePlatformAdminAsync(cancellationToken);
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var tenant = await dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);

        if (tenant is null)
            return Error.NotFound("Tenant.NotFound", $"Tenant '{request.TenantId}' was not found.");

        var tenantId = tenant.Id.ToString();

        var students = await dbContext.Students
            .IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == tenantId && s.DeletedAtUtc == null, cancellationToken);

        var teachers = await dbContext.Teachers
            .IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == tenantId && t.DeletedAtUtc == null, cancellationToken);

        var branches = await dbContext.Branches
            .IgnoreQueryFilters()
            .CountAsync(b => b.TenantId == tenantId && b.DeletedAtUtc == null, cancellationToken);

        var users = await dbContext.TenantMemberships
            .IgnoreQueryFilters()
            .CountAsync(
                m => m.TenantId == tenantId && m.Status == TenantMembershipStatus.Active,
                cancellationToken);

        var counter = await dbContext.TenantUsageCounters
            .FirstOrDefaultAsync(c => c.Id == tenant.Id, cancellationToken);

        if (counter is null)
        {
            dbContext.TenantUsageCounters.Add(TenantUsageCounter.Create(
                tenant.Id,
                students,
                users,
                branches,
                teachers,
                storageUsedMB: 0,
                smsUsedThisCycle: 0,
                effectiveMaxStudents: 0,
                effectiveMaxUsers: 0,
                effectiveMaxBranches: 0,
                effectiveMaxTeachers: 0,
                calculatedAt: DateTime.UtcNow));
        }
        else
        {
            counter.UpdateCounts(
                students,
                users,
                branches,
                teachers,
                counter.StorageUsedMB,
                counter.SMSUsedThisCycle);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subscription.ReconcileUsage",
            entityType: nameof(TenantUsageCounter),
            entityId: tenant.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                Students = students,
                Users = users,
                Branches = branches,
                Teachers = teachers
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
