namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Subscriptions;

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
            // Task 21 (final closure, requirement 16): Invoice money is decimal(18,2), the same
            // precision as every other stored amount on the commercial chain (Offer, Contract,
            // Installment, Payment, PaymentAllocation, Refund, RefundAllocation,
            // CustomerLedgerEntry). At decimal(10,2) an Invoice could not even STORE the
            // ContractedAmount of any Contract whose GrossAmount exceeded 99,999,999.99, which
            // made the mandatory invariant Invoice.TotalAmount == Contract.ContractedAmount
            // false at the database level for perfectly valid commercial data.
            // The rounding policy is NOT changed: amounts are still rounded to 2 decimals
            // exactly once (see CreateInvoiceFromBillingCycleCommand). Widening the COLUMN
            // precision cannot introduce a new rounding error, because decimal(18,2) holds a
            // strict superset of the values decimal(10,2) holds.
            .HasPrecision(18, 2);

        builder.Property(i => i.DiscountAmount)
            .HasPrecision(18, 2);

        builder.Property(i => i.TaxAmount)
            .HasPrecision(18, 2);

        builder.Property(i => i.TotalAmount)
            .HasPrecision(18, 2);

        builder.Property(i => i.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(i => i.IssuedAt);

        builder.Property(i => i.DueAt);

        builder.Property(i => i.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(i => i.ContractId)
            .HasColumnType("uniqueidentifier");

        builder.Property(i => i.SubscriptionId)
            .HasColumnType("uniqueidentifier");

        builder.Property(i => i.BillingCycleId)
            .HasColumnType("uniqueidentifier");

        // ── CRITICAL FIX: at most ONE invoice per BillingCycle, enforced by the DATABASE ────
        // BillingCycle.MarkInvoiced() alone is application-level state and cannot stop two
        // concurrent requests that both read the cycle while it was still Draft.
        // BillingCycleId is legitimately NULL for manually created invoices (CreateInvoiceCommand
        // passes null), so the constraint MUST be a FILTERED unique index: a plain unique index
        // would collapse every manual invoice into one row.
        builder.HasIndex(i => i.BillingCycleId)
            .IsUnique()
            .HasFilter("[BillingCycleId] IS NOT NULL")
            .HasDatabaseName("UX_Invoices_BillingCycleId");

        // ── Commercial-chain foreign keys ──────────────────────────────────────────────────
        // Invoice → Contract → Subscription → BillingCycle must resolve to real rows.
        // All three FKs are optional (Guid?) so legacy/manual invoices without a full chain
        // remain valid; the filtered unique index above covers the mandatory chain links.
        // Restrict (NO ACTION): an invoice's commercial history must never be deleted by a
        // cascade from its principal row.
        builder.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(i => i.ContractId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Invoices_Contracts_ContractId");

        builder.HasOne<TenantPlan>()
            .WithMany()
            .HasForeignKey(i => i.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Invoices_TenantPlans_SubscriptionId");

        builder.HasOne<BillingCycle>()
            .WithMany()
            .HasForeignKey(i => i.BillingCycleId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Invoices_BillingCycles_BillingCycleId");

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

        // Optimistic-concurrency token (SQL Server rowversion, store-generated)
        builder.Property(i => i.RowVersion)
            .IsRowVersion();

        builder.HasIndex(i => i.TenantId);
        builder.HasIndex(i => new { i.TenantId, i.Status });
        builder.HasIndex(i => new { i.TenantId, i.PeriodStart, i.PeriodEnd });
        builder.HasIndex(i => i.ContractId);
        builder.HasIndex(i => i.SubscriptionId);
        // NOTE: BillingCycleId is indexed by UX_Invoices_BillingCycleId (filtered, unique) above.
    }
}
