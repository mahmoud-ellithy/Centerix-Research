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
        builder.Property(tp => tp.SnapshotPrice).HasPrecision(10, 2);
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

        // DATABASE-LEVEL single-non-terminal-subscription invariant: at most one Active or
        // Suspended subscription per tenant. History rows (Expired/Cancelled/Pending) do not
        // participate. Application checks remain as defense in depth only.
        builder.HasIndex(tp => tp.TenantId)
            .HasFilter($"[{nameof(TenantPlan.Status)}] IN (1, 4)")
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
