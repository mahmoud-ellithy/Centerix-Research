namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for ContractFeature — immutable per-contract feature entitlement snapshot.
/// Tenant-scoped via the parent Contract (no global query filter needed on this child).
/// </summary>
public class ContractFeatureConfiguration : IEntityTypeConfiguration<ContractFeature>
{
    public void Configure(EntityTypeBuilder<ContractFeature> builder)
    {
        builder.ToTable("ContractFeatures", "Platform");

        builder.HasKey(cf => cf.Id);

        builder.Property(cf => cf.ContractId)
            .IsRequired();

        builder.Property(cf => cf.FeatureCode)
            .HasMaxLength(256)
            .IsRequired();

        // Unique feature code per contract
        builder.HasIndex(cf => new { cf.ContractId, cf.FeatureCode })
            .IsUnique()
            .HasDatabaseName("UX_ContractFeatures_ContractId_FeatureCode");

        // Navigation
        builder.HasOne(cf => cf.Contract)
            .WithMany(c => c.ContractFeatures)
            .HasForeignKey(cf => cf.ContractId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
