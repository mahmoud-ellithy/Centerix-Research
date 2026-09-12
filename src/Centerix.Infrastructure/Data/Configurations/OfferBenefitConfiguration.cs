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
