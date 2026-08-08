using Centerix.Domain.Auditing;
using Centerix.Domain.Authentication;
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

using Microsoft.EntityFrameworkCore;

namespace Centerix.Application.Common.Interfaces;

public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }
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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
