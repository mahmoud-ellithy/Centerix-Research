namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantPlanConfiguration : IEntityTypeConfiguration<TenantPlan>
{
    public void Configure(EntityTypeBuilder<TenantPlan> builder)
    {
        builder.ToTable("TenantPlans", "Platform");

        builder.HasKey(tp => tp.Id);

        // Optimistic concurrency: subscription state changes (renew/suspend/expire races) are
        // serialized by SQL Server rowversion instead of silent last-write-wins.
        builder.Property(tp => tp.RowVersion)
            .IsRowVersion();

        // Commercial snapshot — frozen at creation/renewal, never derived from the live Plan.
        // Precision: decimal(18,6) provides sufficient precision for division results.
        // Example: 10,000 / 12 = 833.333333... needs 6 decimal places to avoid rounding drift.
        // With decimal(18,6): 833.333333 × 12 = 9,999.999996 (minimal drift within tolerance).
        // For scenarios requiring exact totals, use Contract.ContractedAmount as the authoritative value.
        builder.Property(tp => tp.SnapshotPrice).HasPrecision(18, 6);
        builder.Property(tp => tp.SnapshotMonthlyCharge).HasPrecision(18, 6);
        builder.Property(tp => tp.SnapshotCurrency)
            .HasMaxLength(3)
            .IsRequired();
        builder.Property(tp => tp.DurationMonths).IsRequired();
        builder.Property(tp => tp.BonusMonths).IsRequired();

        builder.Property(tp => tp.StartsAtUtc).IsRequired();
        builder.Property(tp => tp.BaseEndsAtUtc).IsRequired();
        builder.Property(tp => tp.EffectiveEndsAtUtc).IsRequired();

        builder.HasOne(tp => tp.Plan)
            .WithMany(p => p.TenantPlans)
            .HasForeignKey(tp => tp.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        // Explicit FK to Contract: a subscription may be linked to a commercial agreement.
        // DeleteBehavior.Restrict: a contract must not be deleted while it has active subscriptions.
        builder.HasOne(tp => tp.Contract)
            .WithMany(c => c.Subscriptions)
            .HasForeignKey(tp => tp.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for FK lookups
        builder.HasIndex(tp => tp.ContractId)
            .HasDatabaseName("IX_TenantPlans_ContractId");

        builder.Property(tp => tp.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(tp => tp.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(tp => tp.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(tp => tp.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(tp => tp.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        // DATABASE-LEVEL single-non-terminal-subscription invariant: at most one Active,
        // Suspended, or PastDue subscription per tenant. History rows (Expired/Cancelled/Pending)
        // do not participate. PastDue is non-terminal because the subscription can recover to
        // Active upon settlement of overdue obligations.
        builder.HasIndex(tp => tp.TenantId)
            .HasFilter($"[{nameof(TenantPlan.Status)}] IN (1, 4, 5)")
            .IsUnique()
            .HasDatabaseName("UX_TenantPlans_TenantId_NonTerminalStatus");

        // Fast path for "current subscription for tenant" resolution.
        builder.HasIndex(tp => new { tp.TenantId, tp.Status })
            .HasDatabaseName("IX_TenantPlans_TenantId_Status");

        builder.HasIndex(tp => tp.EffectiveEndsAtUtc)
            .HasDatabaseName("IX_TenantPlans_EffectiveEndsAtUtc");

        builder.Navigation(tp => tp.Features).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
