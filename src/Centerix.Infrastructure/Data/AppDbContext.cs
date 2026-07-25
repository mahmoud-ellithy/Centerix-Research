using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Auditing;
using Centerix.Domain.Authentication;
using Centerix.Domain.Common;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Billing;
using Centerix.Domain.Platform.Leads;
using Centerix.Domain.Platform.Auditing;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Students.Attendance;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;
using MediatR;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;

namespace Centerix.Infrastructure.Data;

public class AppDbContext : IdentityDbContext, IAppDbContext
{
    private readonly IMediator _mediator;
    private readonly ICurrentTenant _currentTenant;
    private string? _currentTenantId;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        IMediator mediator,
        ICurrentTenant currentTenant) : base(options)
    {
        _mediator = mediator;
        _currentTenant = currentTenant;
        _currentTenantId = _currentTenant.IsResolved ? _currentTenant.TenantId : null;
    }

    public DbSet<Plan> Plans { get; set; } = default!;
    public DbSet<Feature> Features { get; set; } = default!;
    public DbSet<PlanFeature> PlanFeatures { get; set; } = default!;
    public DbSet<TenantPlan> TenantPlans { get; set; } = default!;
    public DbSet<TenantBilling> TenantBillings { get; set; } = default!;
    public DbSet<PlatformAuditLog> PlatformAuditLogs { get; set; } = default!;
    public DbSet<TenantCRMLead> TenantCRMLeads { get; set; } = default!;
    public DbSet<Permission> Permissions { get; set; } = default!;
    public DbSet<RolePermission> RolePermissions { get; set; } = default!;
    public DbSet<AuditLog> AuditLogs { get; set; } = default!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = default!;

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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyTenantQueryFilter(builder);
    }

    private void ApplyTenantQueryFilter(ModelBuilder builder)
    {
        // When tenant is resolved, filter by tenant ID.
        // When tenant is NOT resolved, apply a filter that returns no results (fail-closed).
        var tenantId = _currentTenantId ?? "__NO_ACCESS__";

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(IHasTenantId).IsAssignableFrom(entityType.ClrType) && !entityType.IsOwned())
            {
                var parameter = Expression.Parameter(entityType.ClrType, "e");
                var property = Expression.Property(parameter, nameof(IHasTenantId.TenantId));
                var constant = Expression.Constant(tenantId);
                var equal = Expression.Equal(property, constant);
                var lambda = Expression.Lambda(equal, parameter);
                builder.Entity(entityType.ClrType).HasQueryFilter(lambda);
            }
        }
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
