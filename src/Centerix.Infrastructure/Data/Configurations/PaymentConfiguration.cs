namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Payments;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments", "Platform");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("PaymentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(p => p.PaymentNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(p => p.PaymentNumber)
            .IsUnique()
            .HasDatabaseName("UX_Payments_PaymentNumber");

        builder.Property(p => p.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(p => p.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(p => p.Method)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.CompletedAtUtc)
            .HasColumnType("datetime2");

        builder.Property(p => p.ExternalReference)
            .HasMaxLength(200);

        builder.HasIndex(p => p.ExternalReference)
            .HasDatabaseName("IX_Payments_ExternalReference");

        builder.Property(p => p.Notes)
            .HasMaxLength(500);

        builder.Property(p => p.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(p => p.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(p => p.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(p => p.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(p => p.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);

        builder.Property(p => p.RowVersion)
            .IsRowVersion();

        // Task 19 — client-supplied idempotency key, unique per tenant.
        // Filtered unique index allows NULL IdempotencyKey (legacy / pre-19 rows) while
        // preventing duplicate execution of the same logical request within a tenant.
        builder.Property(p => p.IdempotencyKey)
            .HasMaxLength(256);

        builder.HasIndex(p => new { p.TenantId, p.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName("UX_Payments_TenantId_IdempotencyKey");

        builder.HasIndex(p => p.TenantId);
        builder.HasIndex(p => new { p.TenantId, p.Status });
        builder.HasIndex(p => new { p.TenantId, p.CompletedAtUtc });

        // Allocations
        builder.HasMany(p => p.Allocations)
            .WithOne(a => a.Payment)
            .HasForeignKey(a => a.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Receipts
        builder.HasMany(p => p.Receipts)
            .WithOne(r => r.Payment)
            .HasForeignKey(r => r.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
