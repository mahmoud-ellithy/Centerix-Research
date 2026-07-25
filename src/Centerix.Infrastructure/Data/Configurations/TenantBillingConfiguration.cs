namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantBillingConfiguration : IEntityTypeConfiguration<TenantBilling>
{
    public void Configure(EntityTypeBuilder<TenantBilling> builder)
    {
        builder.ToTable("TenantBilling", "Platform");

        builder.HasKey(tb => tb.Id);

        builder.HasOne(tb => tb.Plan)
            .WithMany(p => p.TenantBillings)
            .HasForeignKey(tb => tb.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(tb => tb.AmountEGP)
            .HasPrecision(10, 2);

        builder.Property(tb => tb.Method)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(tb => tb.InvoiceRef)
            .HasMaxLength(100);

        builder.Property(tb => tb.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(tb => tb.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(tb => tb.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(tb => tb.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(tb => tb.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(tb => tb.TenantId);
    }
}
