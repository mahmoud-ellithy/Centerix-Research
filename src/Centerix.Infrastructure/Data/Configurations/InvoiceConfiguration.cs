namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Invoicing;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices", "Platform");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id)
            .HasColumnName("InvoiceId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(i => i.InvoiceNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(i => i.InvoiceNumber)
            .IsUnique()
            .HasDatabaseName("UX_Invoices_InvoiceNumber");

        builder.Property(i => i.PeriodStart)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(i => i.PeriodEnd)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(i => i.Subtotal)
            .HasPrecision(10, 2);

        builder.Property(i => i.DiscountAmount)
            .HasPrecision(10, 2);

        builder.Property(i => i.TaxAmount)
            .HasPrecision(10, 2);

        builder.Property(i => i.TotalAmount)
            .HasPrecision(10, 2);

        builder.Property(i => i.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(i => i.IssuedAt);

        builder.Property(i => i.DueAt);

        builder.Property(i => i.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(i => i.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(i => i.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(i => i.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(i => i.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(i => i.TenantId);
        builder.HasIndex(i => new { i.TenantId, i.Status });
        builder.HasIndex(i => new { i.TenantId, i.PeriodStart, i.PeriodEnd });
    }
}
