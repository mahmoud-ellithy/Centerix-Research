namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Credits;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantCreditConfiguration : IEntityTypeConfiguration<TenantCredit>
{
    public void Configure(EntityTypeBuilder<TenantCredit> builder)
    {
        builder.ToTable("TenantCredits", "Platform");

        builder.HasKey(tc => tc.Id);

        builder.Property(tc => tc.Id)
            .HasColumnName("TenantCreditId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(tc => tc.Amount)
            .HasPrecision(10, 2);

        builder.Property(tc => tc.RemainingAmount)
            .HasPrecision(10, 2);

        // Task 18.5 — immutable economic-origin lineage: the portion of Amount that is
        // customer-paid value transferred from prior SubscriptionChange credits.
        // The invariant 0 <= TransferredPaidAmount <= Amount is additionally enforced by
        // CK_TenantCredits_TransferredPaidAmount_Bounded (see Task18_5 migration).
        builder.Property(tc => tc.TransferredPaidAmount)
            .HasPrecision(10, 2);

        // Computed lineage classifiers — derived from SourceType/TransferredPaidAmount,
        // never persisted.
        builder.Ignore(tc => tc.DirectPaidAmount);
        builder.Ignore(tc => tc.CustomerPaidEconomicValue);

        builder.Property(tc => tc.SourceType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(tc => tc.SourceId)
            .HasColumnType("uniqueidentifier");

        builder.Property(tc => tc.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(tc => tc.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired()
            .HasDefaultValue("EGP");

        builder.Property(tc => tc.ReversalOfCreditId)
            .HasColumnType("uniqueidentifier");

        builder.Property(tc => tc.IdempotencyKey)
            .HasMaxLength(200);

        builder.Property(tc => tc.RowVersion)
            .IsRowVersion();

        builder.Property(tc => tc.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(tc => tc.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(tc => tc.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(tc => tc.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(tc => tc.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(tc => tc.TenantId);
        builder.HasIndex(tc => new { tc.TenantId, tc.Status });

        // Unique constraint: one credit per (Tenant, SourceType, SourceId) combination
        // Prevents duplicate credits from concurrent requests
        builder.HasIndex(tc => new { tc.TenantId, tc.SourceType, tc.SourceId })
            .IsUnique()
            .HasDatabaseName("UX_TenantCredits_TenantId_SourceType_SourceId")
            .HasFilter("[SourceId] IS NOT NULL");

        // Task 19 — client-supplied idempotency key, unique per tenant.
        // Filtered unique index allows NULL IdempotencyKey (legacy / pre-19 rows and
        // SourceId-keyed credits covered by the structural constraint above) while
        // preventing duplicate execution of the same logical request within a tenant.
        builder.HasIndex(tc => new { tc.TenantId, tc.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName("UX_TenantCredits_TenantId_IdempotencyKey");
    }
}
