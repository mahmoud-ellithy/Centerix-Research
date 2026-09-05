namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for ContractPricingTier entity.
/// Immutable snapshot of pricing terms agreed at contract creation.
/// </summary>
public class ContractPricingTierConfiguration : IEntityTypeConfiguration<ContractPricingTier>
{
    public void Configure(EntityTypeBuilder<ContractPricingTier> builder)
    {
        builder.ToTable("ContractPricingTiers", "Platform");

        builder.HasKey(pt => pt.Id);

        builder.HasOne(pt => pt.Contract)
            .WithMany(c => c.PricingTiers)
            .HasForeignKey(pt => pt.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(pt => pt.DurationMonths)
            .IsRequired();

        builder.Property(pt => pt.TierPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(pt => pt.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(pt => pt.MonthlyListPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(pt => pt.DisplayOrder)
            .IsRequired();

        // Unique tier duration per contract
        builder.HasIndex(pt => new { pt.ContractId, pt.DurationMonths })
            .IsUnique()
            .HasDatabaseName("UX_ContractPricingTiers_ContractId_DurationMonths");

        builder.HasIndex(pt => pt.ContractId)
            .HasDatabaseName("IX_ContractPricingTiers_ContractId");
    }
}
