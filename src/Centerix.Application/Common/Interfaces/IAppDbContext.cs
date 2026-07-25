using Centerix.Domain.Auditing;
using Centerix.Domain.Authentication;
using Centerix.Domain.Platform.Auditing;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Billing;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Leads;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}