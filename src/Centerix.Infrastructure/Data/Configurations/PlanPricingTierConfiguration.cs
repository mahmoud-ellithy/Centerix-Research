namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for PlanPricingTier entity.
/// Authoritative pricing tier at the Plan catalog level.
/// </summary>
public class PlanPricingTierConfiguration : IEntityTypeConfiguration<PlanPricingTier>
{
    public void Configure(EntityTypeBuilder<PlanPricingTier> builder)
    {
        builder.ToTable("PlanPricingTiers", "Platform");

        builder.HasKey(pt => pt.Id);

        builder.HasOne(pt => pt.Plan)
            .WithMany()
            .HasForeignKey(pt => pt.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(pt => pt.DurationMonths)
            .IsRequired();

        builder.Property(pt => pt.TierPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(pt => pt.DisplayOrder)
            .IsRequired();

        // Unique tier duration per plan
        builder.HasIndex(pt => new { pt.PlanId, pt.DurationMonths })
            .IsUnique()
            .HasDatabaseName("UX_PlanPricingTiers_PlanId_DurationMonths");

        builder.HasIndex(pt => pt.PlanId)
            .HasDatabaseName("IX_PlanPricingTiers_PlanId");
    }
}
