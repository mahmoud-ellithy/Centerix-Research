namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the Contract aggregate.
/// Tenant-scoped entity with commercial snapshot semantics.
/// </summary>
public class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        builder.ToTable("Contracts", "Platform");

        builder.HasKey(c => c.Id);

        // TenantId is INHERITED from AuditableEntity<T> (IHasTenantId): it drives the global
        // tenant query filter and must not be shadowed.
        builder.Property(c => c.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        // Contract number: unique within a tenant scope
        builder.Property(c => c.ContractNumber)
            .HasMaxLength(50)
            .IsRequired();

        // Unique contract number per tenant
        builder.HasIndex(c => new { c.TenantId, c.ContractNumber })
            .IsUnique()
            .HasDatabaseName("UX_Contracts_TenantId_ContractNumber");

        // Status
        builder.Property(c => c.Status)
            .HasConversion<byte>()
            .IsRequired();

        // Plan reference (global catalog entity)
        builder.HasIndex(c => c.PlanId)
            .HasDatabaseName("IX_Contracts_PlanId");

        // Dates
        builder.Property(c => c.EffectiveAtUtc)
            .IsRequired();

        builder.Property(c => c.EndsAtUtc)
            .IsRequired();

        builder.Property(c => c.DurationMonths)
            .IsRequired();

        // Commercial snapshot: monetary values
        builder.Property(c => c.MonthlyListPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(c => c.ContractualMonthlyValue)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(c => c.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(c => c.ContractedAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(c => c.DiscountAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(c => c.PromotionReference)
            .HasMaxLength(200);

        // Indexes
        builder.HasIndex(c => new { c.TenantId, c.Status })
            .HasDatabaseName("IX_Contracts_TenantId_Status");

        builder.HasIndex(c => c.EffectiveAtUtc)
            .HasDatabaseName("IX_Contracts_EffectiveAtUtc");

        builder.HasIndex(c => c.EndsAtUtc)
            .HasDatabaseName("IX_Contracts_EndsAtUtc");

        // Navigation: Pricing Tiers (cascade delete)
        builder.HasMany(c => c.PricingTiers)
            .WithOne(pt => pt.Contract)
            .HasForeignKey(pt => pt.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.PricingTiers)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Navigation: Benefits (cascade delete)
        builder.HasMany(c => c.Benefits)
            .WithOne(b => b.Contract)
            .HasForeignKey(b => b.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.Benefits)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Navigation: Subscriptions (TenantPlans linked via explicit foreign key)
        builder.HasMany(c => c.Subscriptions)
            .WithOne(tp => tp.Contract)
            .HasForeignKey(tp => tp.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(c => c.Subscriptions)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
