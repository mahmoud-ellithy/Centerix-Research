namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Subscriptions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class InstallmentConfiguration : IEntityTypeConfiguration<Installment>
{
    public void Configure(EntityTypeBuilder<Installment> builder)
    {
        builder.ToTable("Installments", "Platform");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id)
            .HasColumnName("InstallmentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(i => i.ContractId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(i => i.SubscriptionId)
            .HasColumnType("uniqueidentifier");

        // FK: Installment.SubscriptionId → TenantPlan.Id (optional — historical data may have null).
        // Restrict: a subscription must not be deleted while it has linked installments.
        builder.HasOne<TenantPlan>()
            .WithMany()
            .HasForeignKey(i => i.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(i => i.InvoiceId)
            .HasColumnType("uniqueidentifier");

        builder.Property(i => i.SequenceNumber)
            .IsRequired();

        builder.Property(i => i.DueDateUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(i => i.CoveredPeriodStartUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(i => i.CoveredPeriodEndUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(i => i.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(i => i.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(i => i.SettledAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(i => i.Status)
            .HasConversion<byte>()
            .IsRequired();

        builder.Property(i => i.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(i => i.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(i => i.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(i => i.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(i => i.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);

        builder.Property(i => i.RowVersion)
            .IsRowVersion();

        // Indexes
        builder.HasIndex(i => i.TenantId);
        builder.HasIndex(i => i.ContractId);
        builder.HasIndex(i => i.SubscriptionId);
        builder.HasIndex(i => i.InvoiceId);
        builder.HasIndex(i => new { i.TenantId, i.ContractId });
        builder.HasIndex(i => new { i.TenantId, i.SubscriptionId });
        builder.HasIndex(i => new { i.TenantId, i.DueDateUtc });
        builder.HasIndex(i => new { i.TenantId, i.Status });

        // Unique: one sequence number per contract within a tenant
        builder.HasIndex(i => new { i.TenantId, i.ContractId, i.SequenceNumber })
            .IsUnique()
            .HasDatabaseName("UX_Installments_TenantContractSequence");

        // Navigation: PaymentAllocations
        builder.HasMany(i => i.PaymentAllocations)
            .WithOne(a => a.Installment)
            .HasForeignKey(a => a.InstallmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(i => i.PaymentAllocations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
