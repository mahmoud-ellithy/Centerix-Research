using System.Linq.Expressions;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Auditing;
using Centerix.Domain.Authentication;
using Centerix.Domain.Common;
using Centerix.Domain.Platform.Auditing;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Leads;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Operations;
using Centerix.Domain.Platform.Referrals;
using Centerix.Domain.Platform.Staff;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Subscriptions.AddOns;
using Centerix.Domain.Platform.Subscriptions.LimitOverrides;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Students.Attendance;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;

using MediatR;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Centerix.Infrastructure.Data;

public class AppDbContext : IdentityDbContext, IAppDbContext
{
    private readonly IMediator _mediator;
    private readonly ICurrentTenant _currentTenant;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        IMediator mediator,
        ICurrentTenant currentTenant) : base(options)
    {
        _mediator = mediator;
        _currentTenant = currentTenant;
    }

    public DbSet<Tenant> Tenants { get; set; } = default!;
    public DbSet<Plan> Plans { get; set; } = default!;
    public DbSet<Feature> Features { get; set; } = default!;
    public DbSet<PlanFeature> PlanFeatures { get; set; } = default!;
    public DbSet<TenantPlan> TenantPlans { get; set; } = default!;
    public DbSet<TenantPlanFeature> TenantPlanFeatures { get; set; } = default!;
    public DbSet<Invoice> Invoices { get; set; } = default!;
    public DbSet<InvoiceLine> InvoiceLines { get; set; } = default!;
    public DbSet<PlatformPayment> PlatformPayments { get; set; } = default!;
    public DbSet<TenantCredit> TenantCredits { get; set; } = default!;
    public DbSet<PlatformAuditLog> PlatformAuditLogs { get; set; } = default!;
    public DbSet<TenantCRMLead> TenantCRMLeads { get; set; } = default!;
    public DbSet<Permission> Permissions { get; set; } = default!;
    public DbSet<RolePermission> RolePermissions { get; set; } = default!;
    public DbSet<AuditLog> AuditLogs { get; set; } = default!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = default!;

    // User <-> Tenant membership (foundation for tenant access control)
    public DbSet<TenantMembership> TenantMemberships { get; set; } = default!;

    // Tenant invitations
    public DbSet<TenantInvitation> TenantInvitations { get; set; } = default!;

    // Platform Staff (ERD v3)
    public DbSet<PlatformUser> PlatformUsers { get; set; } = default!;
    public DbSet<PlatformRole> PlatformRoles { get; set; } = default!;
    public DbSet<PlatformPermission> PlatformPermissions { get; set; } = default!;
    public DbSet<PlatformUserRole> PlatformUserRoles { get; set; } = default!;
    public DbSet<PlatformRolePermission> PlatformRolePermissions { get; set; } = default!;
    public DbSet<ImpersonationLog> ImpersonationLogs { get; set; } = default!;

    // Phase 4: Subscriptions & Add-ons
    public DbSet<AddOnCatalog> AddOnCatalogs { get; set; } = default!;
    public DbSet<AddOnPricingTier> AddOnPricingTiers { get; set; } = default!;
    public DbSet<TenantAddOn> TenantAddOns { get; set; } = default!;
    public DbSet<TenantUsageCounter> TenantUsageCounters { get; set; } = default!;
    public DbSet<TenantLimitOverride> TenantLimitOverrides { get; set; } = default!;

    // Phase 5: Referrals
    public DbSet<TenantReferralCode> TenantReferralCodes { get; set; } = default!;
    public DbSet<TenantReferral> TenantReferrals { get; set; } = default!;

    // Phase 6: Operations
    public DbSet<TenantSetting> TenantSettings { get; set; } = default!;
    public DbSet<TenantProvisioningJob> TenantProvisioningJobs { get; set; } = default!;
    public DbSet<TenantSchemaVersion> TenantSchemaVersions { get; set; } = default!;

    // Education module (M-01)
    public DbSet<Branch> Branches { get; set; } = default!;
    public DbSet<AcademicStage> AcademicStages { get; set; } = default!;
    public DbSet<AcademicYear> AcademicYears { get; set; } = default!;
    public DbSet<Student> Students { get; set; } = default!;
    public DbSet<AttendanceLog> AttendanceLogs { get; set; } = default!;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await DispatchDomainEventsAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }


    public void StampAddedTenantIds(string tenantId)
    {
        if (string.IsNullOrEmpty(tenantId)) return;

        foreach (var entry in ChangeTracker.Entries<IHasTenantId>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(IHasTenantId.TenantId)).CurrentValue = tenantId;
            }
        }
    }

    public bool IsRelational => Database.IsRelational();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => Database.BeginTransactionAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyTenantQueryFilter(builder);
    }

    private void ApplyTenantQueryFilter(ModelBuilder builder)
    {
        // The VERIFIED tenant is read LIVE (per request) from ICurrentTenant.TenantId.
        // Until the pipeline authorizes the resolved tenant (TenantGuardMiddleware calls
        // AuthorizeTenant), TenantId is empty, so the filter matches nothing (fail-closed).
        // We use a C# lambda over a context member (not a baked Expression.Constant) so that EF
        // evaluates _currentTenant.TenantId against the EXECUTING context at query time. This gives
        // correct per-request tenant isolation even though the EF model is cached.
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(IHasTenantId).IsAssignableFrom(entityType.ClrType) && !entityType.IsOwned())
            {
                var apply = typeof(AppDbContext)
                    .GetMethod(nameof(ApplyFilterFor), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType);
                apply.Invoke(this, new object[] { builder });
            }
        }
    }

    private void ApplyFilterFor<TEntity>(ModelBuilder builder)
        where TEntity : class, IHasTenantId
    {
        builder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == _currentTenant.TenantId);
    }

    private async Task DispatchDomainEventsAsync(CancellationToken cancellationToken)
    {
        var domainEntities = ChangeTracker.Entries()
            .Where(e => e.Entity is Entity baseEntity && baseEntity.DomainEvents.Count != 0)
            .Select(e => (Entity)e.Entity)
            .ToList();

        var domainEvents = domainEntities
            .SelectMany(e => e.DomainEvents)
            .ToList();

        foreach (var domainEvent in domainEvents)
        {
            await _mediator.Publish(domainEvent, cancellationToken);
        }

        foreach (var entity in domainEntities)
        {
            entity.ClearDomainEvents();
        }
    }
}

