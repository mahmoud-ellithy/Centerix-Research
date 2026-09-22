namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Promotions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for OfferBenefit entity.
/// Immutable snapshot of benefits attached to an Offer, used as the authoritative
/// source when creating a Contract from an Accepted Offer.
/// </summary>
public class OfferBenefitConfiguration : IEntityTypeConfiguration<OfferBenefit>
{
    public void Configure(EntityTypeBuilder<OfferBenefit> builder)
    {
        builder.ToTable("OfferBenefits", "Platform");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.OfferId)
            .IsRequired();

        builder.Property(b => b.BenefitType)
            .HasConversion<byte>()
            .IsRequired();

        builder.Property(b => b.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(b => b.Description)
            .HasMaxLength(1000);

        builder.Property(b => b.ContractualValue)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(b => b.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.HasIndex(b => b.OfferId)
            .HasDatabaseName("IX_OfferBenefits_OfferId");
    }
}

/// <summary>
/// EF Core configuration for OfferFeature entity.
/// Immutable snapshot of feature entitlements attached to an Offer, used as the
/// authoritative source when creating a Contract from an Accepted Offer.
/// </summary>
public class OfferFeatureConfiguration : IEntityTypeConfiguration<OfferFeature>
{
    public void Configure(EntityTypeBuilder<OfferFeature> builder)
    {
        builder.ToTable("OfferFeatures", "Platform");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.OfferId)
            .IsRequired();

        builder.Property(f => f.FeatureCode)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(f => f.OfferId)
            .HasDatabaseName("IX_OfferFeatures_OfferId");

        // Commercial snapshot integrity: one feature code per Offer, enforced at the
        // database level (not only in application code). FeatureCode is normalized to
        // upper invariant at creation, so a binary/case-sensitive comparison is correct.
        builder.HasIndex(f => new { f.OfferId, f.FeatureCode })
            .IsUnique()
            .HasDatabaseName("UX_OfferFeatures_OfferId_FeatureCode");
    }
}

/// <summary>
/// EF Core configuration for OfferPricingTier entity.
/// Immutable snapshot of pricing tiers attached to an Offer, used as the
/// authoritative source for refund/repricing calculations.
/// </summary>
public class OfferPricingTierConfiguration : IEntityTypeConfiguration<OfferPricingTier>
{
    public void Configure(EntityTypeBuilder<OfferPricingTier> builder)
    {
        builder.ToTable("OfferPricingTiers", "Platform");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.OfferId)
            .IsRequired();

        builder.Property(t => t.DurationMonths)
            .IsRequired();

        builder.Property(t => t.TierPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(t => t.DisplayOrder);

        builder.HasIndex(t => t.OfferId)
            .HasDatabaseName("IX_OfferPricingTiers_OfferId");
    }
}
