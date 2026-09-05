namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Payments;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> builder)
    {
        builder.ToTable("PaymentAllocations", "Platform");

        builder.HasKey(pa => pa.Id);

        builder.Property(pa => pa.Id)
            .HasColumnName("PaymentAllocationId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(pa => pa.PaymentId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(pa => pa.InvoiceId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(pa => pa.AllocatedAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(pa => pa.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(pa => pa.AllocatedAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(pa => pa.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(pa => pa.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(pa => pa.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(pa => pa.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(pa => pa.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);

        builder.Property(pa => pa.RowVersion)
            .IsRowVersion();

        // Relationships
        builder.HasOne(pa => pa.Payment)
            .WithMany(p => p.Allocations)
            .HasForeignKey(pa => pa.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(pa => pa.Invoice)
            .WithMany(i => i.PaymentAllocations)
            .HasForeignKey(pa => pa.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(pa => pa.TenantId);
        builder.HasIndex(pa => pa.PaymentId);
        builder.HasIndex(pa => pa.InvoiceId);
        builder.HasIndex(pa => new { pa.TenantId, pa.InvoiceId });
        builder.HasIndex(pa => new { pa.TenantId, pa.PaymentId });
    }
}
