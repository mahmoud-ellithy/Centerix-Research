namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Payments;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PaymentReceiptConfiguration : IEntityTypeConfiguration<PaymentReceipt>
{
    public void Configure(EntityTypeBuilder<PaymentReceipt> builder)
    {
        builder.ToTable("PaymentReceipts", "Platform");

        builder.HasKey(pr => pr.Id);

        builder.Property(pr => pr.Id)
            .HasColumnName("ReceiptId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(pr => pr.ReceiptNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(pr => pr.ReceiptNumber)
            .IsUnique()
            .HasDatabaseName("UX_PaymentReceipts_ReceiptNumber");

        builder.Property(pr => pr.PaymentId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.HasIndex(pr => pr.PaymentId)
            .IsUnique()
            .HasDatabaseName("UX_PaymentReceipts_PaymentId");

        builder.Property(pr => pr.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(pr => pr.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(pr => pr.Method)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(pr => pr.ExternalReference)
            .HasMaxLength(200);

        builder.Property(pr => pr.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(pr => pr.IssuedAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(pr => pr.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(pr => pr.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(pr => pr.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(pr => pr.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(pr => pr.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);

        builder.Property(pr => pr.RowVersion)
            .IsRowVersion();

        // Relationships
        builder.HasOne(pr => pr.Payment)
            .WithMany(p => p.Receipts)
            .HasForeignKey(pr => pr.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(pr => pr.TenantId);
        builder.HasIndex(pr => new { pr.TenantId, pr.IssuedAtUtc });
    }
}
