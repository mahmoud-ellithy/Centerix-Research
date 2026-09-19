namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Refunds;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the RefundAllocation entity.
/// Links a Refund to its source Payment(s) with amount and method snapshot.
/// </summary>
public class RefundAllocationConfiguration : IEntityTypeConfiguration<RefundAllocation>
{
    public void Configure(EntityTypeBuilder<RefundAllocation> builder)
    {
        builder.ToTable("RefundAllocations", "Platform");

        builder.HasKey(ra => ra.Id);

        builder.Property(ra => ra.Id)
            .HasColumnName("RefundAllocationId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(ra => ra.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        // Refund FK
        builder.Property(ra => ra.RefundId)
            .IsRequired();

        builder.HasIndex(ra => new { ra.TenantId, ra.RefundId })
            .HasDatabaseName("IX_RefundAllocations_TenantId_RefundId");

        // Payment FK
        builder.Property(ra => ra.PaymentId)
            .IsRequired();

        builder.HasIndex(ra => new { ra.TenantId, ra.PaymentId })
            .HasDatabaseName("IX_RefundAllocations_TenantId_PaymentId");

        // Monetary values
        builder.Property(ra => ra.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(ra => ra.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        // Payment method snapshot
        builder.Property(ra => ra.PaymentMethod)
            .HasMaxLength(50)
            .IsRequired();

        // Payment number snapshot (optional)
        builder.Property(ra => ra.PaymentNumber)
            .HasMaxLength(50);

        // Optimistic concurrency token
        builder.Property(ra => ra.RowVersion)
            .IsRowVersion();

        // Prevent duplicate allocation: one refund can only allocate from a payment once
        builder.HasIndex(ra => new { ra.TenantId, ra.RefundId, ra.PaymentId })
            .IsUnique()
            .HasDatabaseName("UX_RefundAllocations_TenantId_RefundId_PaymentId");

        // FK to Refund (restrict delete: a payment with refund history must not become orphaned)
        builder.HasOne<Refund>()
            .WithMany()
            .HasForeignKey(ra => ra.RefundId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK to Payment (restrict delete: a payment with refund allocations must not become orphaned)
        builder.HasOne<Payment>()
            .WithMany()
            .HasForeignKey(ra => ra.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
