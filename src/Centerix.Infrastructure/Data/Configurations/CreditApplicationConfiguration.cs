namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Credits;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class CreditApplicationConfiguration : IEntityTypeConfiguration<CreditApplication>
{
    public void Configure(EntityTypeBuilder<CreditApplication> builder)
    {
        builder.ToTable("CreditApplications", "Platform");

        builder.HasKey(ca => ca.Id);

        builder.Property(ca => ca.Id)
            .HasColumnName("CreditApplicationId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(ca => ca.CreditId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(ca => ca.InvoiceId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(ca => ca.Amount)
            .HasPrecision(10, 2)
            .IsRequired();

        builder.Property(ca => ca.AppliedAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(ca => ca.IdempotencyKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(ca => ca.RowVersion)
            .IsRowVersion();

        builder.Property(ca => ca.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(ca => ca.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(ca => ca.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(ca => ca.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(ca => ca.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);

        // Indexes
        builder.HasIndex(ca => ca.TenantId);
        builder.HasIndex(ca => new { ca.TenantId, ca.CreditId });
        builder.HasIndex(ca => new { ca.TenantId, ca.InvoiceId });
        builder.HasIndex(ca => new { ca.TenantId, ca.CreditId, ca.InvoiceId });

        // Idempotency: unique constraint per tenant so that the same key cannot produce
        // two different credit applications. Empty/null keys (legacy rows) are excluded
        // via a filter so they do not collide.
        builder.HasIndex(ca => new { ca.TenantId, ca.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] <> ''");
    }
}
