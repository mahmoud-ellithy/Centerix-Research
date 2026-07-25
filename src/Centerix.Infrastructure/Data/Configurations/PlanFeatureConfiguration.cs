namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlanFeatureConfiguration : IEntityTypeConfiguration<PlanFeature>
{
    public void Configure(EntityTypeBuilder<PlanFeature> builder)
    {
        builder.ToTable("PlanFeatures", "Platform");

        builder.HasKey(pf => pf.Id);

        builder.HasOne(pf => pf.Plan)
            .WithMany(p => p.PlanFeatures)
            .HasForeignKey(pf => pf.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pf => pf.Feature)
            .WithMany(f => f.PlanFeatures)
            .HasForeignKey(pf => pf.FeatureId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(pf => pf.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(pf => pf.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(pf => pf.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(pf => pf.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);
    }
}
