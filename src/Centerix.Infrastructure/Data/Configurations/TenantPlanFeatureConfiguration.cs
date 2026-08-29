namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantPlanFeatureConfiguration : IEntityTypeConfiguration<TenantPlanFeature>
{
    public void Configure(EntityTypeBuilder<TenantPlanFeature> builder)
    {
        builder.ToTable("TenantPlanFeatures", "Platform");

        builder.HasKey(f => f.Id);

        builder.HasOne(f => f.TenantPlan)
            .WithMany(tp => tp.Features)
            .HasForeignKey(f => f.TenantPlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(f => f.FeatureCode)
            .HasMaxLength(50)
            .IsRequired();

        // One entitlement code per subscription — enforced in the database.
        builder.HasIndex(f => new { f.TenantPlanId, f.FeatureCode })
            .IsUnique()
            .HasDatabaseName("UX_TenantPlanFeatures_PlanId_FeatureCode");

        builder.HasIndex(f => f.FeatureCode)
            .HasDatabaseName("IX_TenantPlanFeatures_FeatureCode");

        builder.Property(f => f.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Navigation(f => f.TenantPlan).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
