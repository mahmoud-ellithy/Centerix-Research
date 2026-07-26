using Centerix.Domain.Auditing;
using Centerix.Domain.Authentication;
using Centerix.Domain.Platform.Auditing;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Billing;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Leads;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Students.Attendance;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;

using Microsoft.EntityFrameworkCore;

namespace Centerix.Application.Common.Interfaces;

public interface IAppDbContext
{
    DbSet<Plan> Plans { get; }
    DbSet<Feature> Features { get; }
    DbSet<PlanFeature> PlanFeatures { get; }
    DbSet<TenantPlan> TenantPlans { get; }
    DbSet<TenantBilling> TenantBillings { get; }
    DbSet<PlatformAuditLog> PlatformAuditLogs { get; }
    DbSet<TenantCRMLead> TenantCRMLeads { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<RefreshToken> RefreshTokens { get; }

    // Education module (M-01)
    DbSet<Branch> Branches { get; }
    DbSet<AcademicStage> AcademicStages { get; }
    DbSet<AcademicYear> AcademicYears { get; }
    DbSet<Student> Students { get; }
    DbSet<AttendanceLog> AttendanceLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
