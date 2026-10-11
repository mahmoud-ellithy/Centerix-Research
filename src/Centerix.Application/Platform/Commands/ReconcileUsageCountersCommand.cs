namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
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
///
/// Maintenance tool, not a request-path operation: it is NOT atomic with concurrent
/// student/teacher/branch/membership creates or releases happening on the same tenant at the
/// same time — the counts it reads are a snapshot, not a transactionally-consistent view
/// against those other writers. Run it during a quiet period for the affected tenant (no
/// in-flight create/release/invitation-accept activity), not as a routine background job.
///
/// Only <see cref="TenantUsageCounter.StudentsCount"/>/<c>UsersCount</c>/<c>BranchesCount</c>/
/// <c>TeachersCount</c> are recomputed here (via <see cref="TenantUsageCounter.UpdateCounts"/>
/// when the row already exists); the <c>EffectiveMax*</c> fields are left untouched on that
/// path by design — <see cref="ILimitService.GetEffectiveMaxAsync"/> is the single source of
/// truth for capacity (TenantLimitOverride → TenantPlan snapshot) and never reads them, so
/// they are not kept in sync here. When this handler has to CREATE a missing counter row (the
/// row should normally already exist from provisioning) it still populates <c>EffectiveMax*</c>
/// with the tenant's current effective max per <see cref="ILimitService.GetEffectiveMaxAsync"/>
/// (0 when no active subscription defines it) rather than hardcoding 0 regardless of the real
/// subscription state — those fields are otherwise unused today, but a reconcile run is not the
/// place to leave data that looks like "this tenant's capacity is zero" when it may not be.
/// </summary>
public record ReconcileUsageCountersCommand(Guid TenantId) : IRequest<Result<Updated>>;

public class ReconcileUsageCountersHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ILimitService limitService,
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
            // Normally provisioned already; this is a repair path. Populate EffectiveMax*
            // from the tenant's real current effective max (0 only when none is defined)
            // instead of hardcoding 0 regardless of subscription state — see the class doc.
            var effectiveMaxStudents = await limitService.GetEffectiveMaxAsync(
                tenantId, LimitTypeCodes.Students, cancellationToken) ?? 0;
            var effectiveMaxUsers = await limitService.GetEffectiveMaxAsync(
                tenantId, LimitTypeCodes.Users, cancellationToken) ?? 0;
            var effectiveMaxBranches = await limitService.GetEffectiveMaxAsync(
                tenantId, LimitTypeCodes.Branches, cancellationToken) ?? 0;
            var effectiveMaxTeachers = await limitService.GetEffectiveMaxAsync(
                tenantId, LimitTypeCodes.Teachers, cancellationToken) ?? 0;

            dbContext.TenantUsageCounters.Add(TenantUsageCounter.Create(
                tenant.Id,
                students,
                users,
                branches,
                teachers,
                storageUsedMB: 0,
                smsUsedThisCycle: 0,
                effectiveMaxStudents,
                effectiveMaxUsers,
                effectiveMaxBranches,
                effectiveMaxTeachers,
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
