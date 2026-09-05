namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Contracts;
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

        builder.Property(b => b.IsGranted)
            .IsRequired();

        builder.Property(b => b.GrantedAtUtc);

        builder.HasIndex(b => b.ContractId)
            .HasDatabaseName("IX_ContractBenefits_ContractId");

        builder.HasIndex(b => new { b.ContractId, b.IsGranted })
            .HasDatabaseName("IX_ContractBenefits_ContractId_IsGranted");
    }
}
