namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for ContractBenefit entity.
/// Records commercial benefits/gifts granted as part of a contract.
/// </summary>
public class ContractBenefitConfiguration : IEntityTypeConfiguration<ContractBenefit>
{
    public void Configure(EntityTypeBuilder<ContractBenefit> builder)
    {
        builder.ToTable("ContractBenefits", "Platform");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.ContractId)
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

        builder.Property(b => b.EligibilityStatus)
            .HasConversion<byte>()
            .IsRequired();

        builder.Property(b => b.EligibleAtUtc);

        builder.Property(b => b.FulfillmentStatus)
            .HasConversion<byte>()
            .IsRequired();

        builder.Property(b => b.GrantedAtUtc);

        builder.Property(b => b.GrantedBy)
            .HasMaxLength(450);

        builder.Property(b => b.DeliveredAtUtc);

        builder.Property(b => b.DeliveredBy)
            .HasMaxLength(450);

        builder.Property(b => b.EligibilityRule)
            .HasConversion(
                rule => rule != null ? EligibilityRuleSerializer.Serialize(rule) : null,
                json => !string.IsNullOrEmpty(json) ? EligibilityRuleSerializer.Deserialize(json) : null)
            .HasMaxLength(4000)
            .IsRequired(false);

        builder.HasIndex(b => b.ContractId)
            .HasDatabaseName("IX_ContractBenefits_ContractId");

        builder.HasIndex(b => new { b.ContractId, b.FulfillmentStatus })
            .HasDatabaseName("IX_ContractBenefits_ContractId_FulfillmentStatus");

        builder.HasIndex(b => new { b.ContractId, b.EligibilityStatus })
            .HasDatabaseName("IX_ContractBenefits_ContractId_EligibilityStatus");
    }
}
