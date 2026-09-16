namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Refunds;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the Refund entity.
/// Tenant-scoped entity representing a separate financial transaction.
/// </summary>
public class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds", "Platform");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("RefundId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        // TenantId is INHERITED from AuditableEntity<T> (IHasTenantId): it drives the global
        // tenant query filter and must not be shadowed.
        builder.Property(r => r.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        // Refund number: unique within a tenant scope
        builder.Property(r => r.RefundNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.RefundNumber })
            .IsUnique()
            .HasDatabaseName("UX_Refunds_TenantId_RefundNumber");

        // Contract reference
        builder.Property(r => r.ContractId)
            .IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.ContractId })
            .HasDatabaseName("IX_Refunds_TenantId_ContractId");

        // Optional subscription reference
        builder.Property(r => r.SubscriptionId);

        // One cancellation refund per subscription. Filtered unique: allows NULL SubscriptionId
        // for non-subscription refunds (e.g., invoice-level refunds) while preventing duplicate
        // cancellation refunds for the same subscription.
        builder.HasIndex(r => new { r.TenantId, r.SubscriptionId })
            .IsUnique()
            .HasFilter("[SubscriptionId] IS NOT NULL")
            .HasDatabaseName("UX_Refunds_TenantId_SubscriptionId_OnePerSubscription");

        // Optional invoice reference
        builder.Property(r => r.InvoiceId);

        builder.HasIndex(r => new { r.TenantId, r.InvoiceId })
            .HasDatabaseName("IX_Refunds_TenantId_InvoiceId");

        // Monetary values
        builder.Property(r => r.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(r => r.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        // Status
        builder.Property(r => r.Status)
            .HasConversion<byte>()
            .IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.Status })
            .HasDatabaseName("IX_Refunds_TenantId_Status");

        // Reason
        builder.Property(r => r.Reason)
            .HasMaxLength(500)
            .IsRequired();

        // Timestamps
        builder.Property(r => r.RequestedAtUtc)
            .IsRequired();

        builder.Property(r => r.ApprovedAtUtc);

        builder.Property(r => r.ExecutedAtUtc);

        // Audit fields
        builder.Property(r => r.CreatedBy)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(r => r.ApprovedBy)
            .HasMaxLength(450);

        builder.Property(r => r.ExecutedBy)
            .HasMaxLength(450);

        // Optimistic concurrency token
        builder.Property(r => r.RowVersion)
            .IsRowVersion();

        // Indexes for common queries
        builder.HasIndex(r => new { r.TenantId, r.RequestedAtUtc })
            .HasDatabaseName("IX_Refunds_TenantId_RequestedAtUtc");

        builder.HasIndex(r => new { r.TenantId, r.ExecutedAtUtc })
            .HasDatabaseName("IX_Refunds_TenantId_ExecutedAtUtc");
    }
}
