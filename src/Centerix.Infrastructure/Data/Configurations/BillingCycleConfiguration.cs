namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Subscriptions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class BillingCycleConfiguration : IEntityTypeConfiguration<BillingCycle>
{
    public void Configure(EntityTypeBuilder<BillingCycle> builder)
    {
        builder.ToTable("BillingCycles", "Platform");

        builder.HasKey(bc => bc.Id);

        builder.Property(bc => bc.Id)
            .HasColumnName("BillingCycleId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(bc => bc.SubscriptionId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(bc => bc.PeriodStart)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(bc => bc.PeriodEnd)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(bc => bc.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(bc => bc.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(bc => bc.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(bc => bc.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(bc => bc.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(bc => bc.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        // Relationship: BillingCycle -> Subscription
        builder.HasOne(bc => bc.Subscription)
            .WithMany()
            .HasForeignKey(bc => bc.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(bc => bc.SubscriptionId)
            .HasDatabaseName("IX_BillingCycles_SubscriptionId");

        builder.HasIndex(bc => bc.TenantId)
            .HasDatabaseName("IX_BillingCycles_TenantId");

        builder.HasIndex(bc => new { bc.TenantId, bc.Status })
            .HasDatabaseName("IX_BillingCycles_TenantId_Status");

        builder.HasIndex(bc => new { bc.SubscriptionId, bc.PeriodStart, bc.PeriodEnd })
            .HasDatabaseName("IX_BillingCycles_SubscriptionId_Period");
    }
}
