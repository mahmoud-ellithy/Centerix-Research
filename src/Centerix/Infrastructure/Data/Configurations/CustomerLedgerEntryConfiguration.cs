namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Payments;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class CustomerLedgerEntryConfiguration : IEntityTypeConfiguration<CustomerLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CustomerLedgerEntry> builder)
    {
        builder.ToTable("CustomerLedgerEntries", "Platform");

        builder.HasKey(le => le.Id);

        builder.Property(le => le.Id)
            .HasColumnName("LedgerEntryId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(le => le.EntryType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(le => le.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(le => le.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(le => le.RunningBalance)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(le => le.InvoiceId)
            .HasColumnType("uniqueidentifier");

        builder.Property(le => le.PaymentId)
            .HasColumnType("uniqueidentifier");

        builder.Property(le => le.CreditId)
            .HasColumnType("uniqueidentifier");

        builder.Property(le => le.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(le => le.RecordedAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(le => le.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(le => le.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(le => le.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(le => le.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(le => le.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);

        builder.Property(le => le.RowVersion)
            .IsRowVersion();

        // Indexes
        builder.HasIndex(le => le.TenantId);
        builder.HasIndex(le => new { le.TenantId, le.RecordedAtUtc });
        builder.HasIndex(le => new { le.TenantId, le.EntryType });
        builder.HasIndex(le => le.InvoiceId);
        builder.HasIndex(le => le.PaymentId);
        builder.HasIndex(le => le.CreditId);
    }
}
