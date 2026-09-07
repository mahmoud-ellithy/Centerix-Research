namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Payments;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class CustomerLedgerEntryConfiguration : IEntityTypeConfiguration<CustomerLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CustomerLedgerEntry> builder)
    {
        builder.ToTable("CustomerLedgerEntries", "Platform");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("LedgerEntryId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(e => e.EntryType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(e => e.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(e => e.RunningBalance)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(e => e.InvoiceId)
            .HasColumnType("uniqueidentifier");

        builder.Property(e => e.PaymentId)
            .HasColumnType("uniqueidentifier");

        builder.Property(e => e.PaymentAllocationId)
            .HasColumnType("uniqueidentifier");

        builder.Property(e => e.RefundId)
            .HasColumnType("uniqueidentifier");

        builder.Property(e => e.CreditId)
            .HasColumnType("uniqueidentifier");

        builder.Property(e => e.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(e => e.RecordedAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(e => e.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(e => e.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(e => e.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(e => e.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(e => e.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);

        builder.Property(e => e.RowVersion)
            .IsRowVersion();

        // Indexes
        builder.HasIndex(e => e.TenantId);
        builder.HasIndex(e => new { e.TenantId, e.RecordedAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.InvoiceId });
        builder.HasIndex(e => new { e.TenantId, e.PaymentId });
        builder.HasIndex(e => new { e.TenantId, e.PaymentAllocationId });

        // Critical: prevent duplicate settlement entries for the same allocation.
        // A PaymentSettlement ledger entry must correspond to exactly one active PaymentAllocation.
        // Filtered unique index guarantees no two active settlement rows reference the same allocation.
        builder.HasIndex(e => new { e.TenantId, e.PaymentAllocationId, e.EntryType })
            .HasFilter("[EntryType] = 'PaymentSettlement' AND [PaymentAllocationId] IS NOT NULL")
            .IsUnique()
            .HasDatabaseName("UX_CustomerLedgerEntries_SettlementByAllocation");

        // Prevent duplicate refund settlements: each RefundSettlement ledger entry must
        // correspond to exactly one refund. Filtered unique index on RefundId guarantees
        // no two settlement rows reference the same refund.
        builder.HasIndex(e => new { e.TenantId, e.RefundId, e.EntryType })
            .HasFilter("[EntryType] = 'RefundSettlement' AND [RefundId] IS NOT NULL")
            .IsUnique()
            .HasDatabaseName("UX_CustomerLedgerEntries_SettlementByRefund");
    }
}
