namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions.AddOns;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AddOnPricingTierConfiguration : IEntityTypeConfiguration<AddOnPricingTier>
{
    public void Configure(EntityTypeBuilder<AddOnPricingTier> builder)
    {
        builder.ToTable("AddOnPricingTiers", "Platform");

        builder.HasKey(t => t.Id);

        builder.HasOne(t => t.AddOnCatalog)
            .WithMany(a => a.PricingTiers)
            .HasForeignKey(t => t.AddOnCatalogId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(t => t.UnitPrice)
            .HasPrecision(10, 2);

        builder.Property(t => t.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(t => t.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(t => t.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(t => t.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(t => t.AddOnCatalogId);
    }
}
