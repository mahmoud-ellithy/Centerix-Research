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

        builder.Property(tp => tp.SnapshotPrice).HasPrecision(10, 2);

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

        builder.HasIndex(tp => tp.TenantId);
    }
}
