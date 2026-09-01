using Centerix.Domain.Auditing;
using Centerix.Domain.Authentication;
using Centerix.Domain.Common;
using Centerix.Domain.Platform.Auditing;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Leads;
using Centerix.Domain.Platform.Operations;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Referrals;
using Centerix.Domain.Platform.Staff;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.AddOns;
using Centerix.Domain.Platform.Subscriptions.LimitOverrides;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Students.Attendance;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;
using Centerix.Domain.Teachers.SalaryPayments;
using Centerix.Domain.Teachers.Subjects;
using Centerix.Domain.Teachers.Teachers;
using Centerix.Domain.Teachers.TeacherRatings;
using Centerix.Domain.Teachers.TeacherSalaryConfigs;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Centerix.Application.Common.Interfaces;

public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }

    // User <-> Tenant membership (C1: tenant access control)
    DbSet<TenantMembership> TenantMemberships { get; }

    // Tenant invitations
    DbSet<TenantInvitation> TenantInvitations { get; }

    // Commercial subscriptions (Phase 2)
    DbSet<Domain.Platform.Subscriptions.TenantPlanFeature> TenantPlanFeatures { get; }

    DbSet<Plan> Plans { get; }
    DbSet<Feature> Features { get; }
    DbSet<PlanFeature> PlanFeatures { get; }
    DbSet<TenantPlan> TenantPlans { get; }
    DbSet<PlatformAuditLog> PlatformAuditLogs { get; }
    DbSet<TenantCRMLead> TenantCRMLeads { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<RefreshToken> RefreshTokens { get; }

    // Phase 4: Subscriptions & Add-ons
    DbSet<AddOnCatalog> AddOnCatalogs { get; }
    DbSet<AddOnPricingTier> AddOnPricingTiers { get; }
    DbSet<TenantAddOn> TenantAddOns { get; }
    DbSet<TenantUsageCounter> TenantUsageCounters { get; }
    DbSet<TenantLimitOverride> TenantLimitOverrides { get; }

    // Phase 5: Referrals
    DbSet<TenantReferralCode> TenantReferralCodes { get; }
    DbSet<TenantReferral> TenantReferrals { get; }

    // Phase 6: Operations
    DbSet<TenantSetting> TenantSettings { get; }
    DbSet<TenantProvisioningJob> TenantProvisioningJobs { get; }
    DbSet<TenantSchemaVersion> TenantSchemaVersions { get; }

    // Billing: Invoicing & Payments
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<PlatformPayment> PlatformPayments { get; }

    // Billing: Credits
    DbSet<TenantCredit> TenantCredits { get; }

    // Platform Staff (ERD v3)
    DbSet<PlatformUser> PlatformUsers { get; }
    DbSet<PlatformRole> PlatformRoles { get; }
    DbSet<PlatformPermission> PlatformPermissions { get; }
    DbSet<PlatformUserRole> PlatformUserRoles { get; }
    DbSet<PlatformRolePermission> PlatformRolePermissions { get; }

    // Education module (M-01)
    DbSet<Branch> Branches { get; }
    DbSet<AcademicStage> AcademicStages { get; }
    DbSet<AcademicYear> AcademicYears { get; }
    DbSet<Student> Students { get; }
    DbSet<AttendanceLog> AttendanceLogs { get; }

    // Education module (M-02)
    DbSet<Subject> Subjects { get; }
    DbSet<Teacher> Teachers { get; }
    DbSet<TeacherSalaryConfig> TeacherSalaryConfigs { get; }
    DbSet<SalaryPayment> SalaryPayments { get; }
    DbSet<TeacherRating> TeacherRatings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps the AUTHORIZED tenant id on every <see cref="IHasTenantId"/> entity currently in the
    /// change tracker with state <c>Added</c>. Mirrors what <c>TenantInterceptor</c> does on
    /// the relational path; the test InMemory provider does not invoke the interceptor, so
    /// tenant-aware handlers call this immediately before <see cref="SaveChangesAsync"/> to
    /// avoid a save-time <c>TenantId required</c> failure. Safe to call when no entities are tracked.
    /// </summary>
    void StampAddedTenantIds(string tenantId);

    /// <summary>
    /// True when the configured provider is relational (e.g. SQL Server); false for the EF
    /// InMemory provider used by fast unit tests (which lacks features such as ExecuteUpdate).
    /// </summary>
    bool IsRelational { get; }

    /// <summary>
    /// Begins a database transaction so multi-step use cases (e.g. identity creation plus
    /// membership provisioning) can commit or roll back atomically.
    /// </summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
