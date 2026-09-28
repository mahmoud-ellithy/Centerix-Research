namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the Offer entity.
/// Tenant-scoped persisted commercial offer snapshot.
/// </summary>
public class OfferConfiguration : IEntityTypeConfiguration<Offer>
{
    public void Configure(EntityTypeBuilder<Offer> builder)
    {
        builder.ToTable("Offers", "Platform");

        builder.HasKey(o => o.Id);

        // TenantId is inherited from AuditableEntity<T> (IHasTenantId)
        builder.Property(o => o.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        // Status (enum → byte)
        builder.Property(o => o.Status)
            .HasConversion<byte>()
            .IsRequired();

        // Plan reference
        builder.HasIndex(o => o.PlanId)
            .HasDatabaseName("IX_Offers_PlanId");

        builder.Property(o => o.DurationMonths)
            .IsRequired();

        // Commercial snapshot
        builder.Property(o => o.BaseAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(o => o.DiscountAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(o => o.FinalAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(o => o.MonthlyListPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(o => o.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        // Entitlement snapshot (captured from the Plan at calculation time).
        // Zero values are legitimate (e.g. BonusMonths = 0); snapshot completeness
        // is expressed via EntitlementSnapshotVersion, never via numeric zeros.
        builder.Property(o => o.BonusMonths)
            .IsRequired();

        builder.Property(o => o.MaxStudents)
            .IsRequired();

        builder.Property(o => o.MaxUsers)
            .IsRequired();

        builder.Property(o => o.MaxBranches)
            .IsRequired();

        builder.Property(o => o.MaxTeachers)
            .IsRequired();

        builder.Property(o => o.StorageGB)
            .IsRequired();

        builder.Property(o => o.SMSQuota)
            .IsRequired();

        builder.Property(o => o.EntitlementSnapshotVersion)
            .IsRequired()
            .HasDefaultValue(0);

        // Promotion snapshot
        builder.Property(o => o.PromotionId);

        builder.Property(o => o.PromotionName)
            .HasMaxLength(200);

        builder.Property(o => o.PromotionCode)
            .HasMaxLength(50);

        builder.Property(o => o.PromotionType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(o => o.DiscountPercentage)
            .HasPrecision(5, 2);

        builder.Property(o => o.ChargedMonths);

        // Task A — Commercial payment mode (explicit commercial decision).
        // Stored as a single-byte column mirroring the PaymentTerms enum.
        // NEVER inferred from PromotionType, BonusMonths, installment rows, or any other field.
        // The column is NOT NULL and has no SQL default: every Offer that reaches the database
        // must supply an explicit value (enforced by Offer.Create at the domain boundary). The
        // migration is safe because the repository is greenfield on this column — no historical
        // rows exist to backfill.
        builder.Property(o => o.PaymentTerms)
            .HasConversion<byte>()
            .IsRequired();

        // Lifecycle timestamps
        builder.Property(o => o.CalculatedAtUtc)
            .IsRequired();

        builder.Property(o => o.ExpiresAtUtc);

        builder.Property(o => o.AcceptedAtUtc);

        builder.Property(o => o.ConvertedAtUtc);

        builder.Property(o => o.ContractId);

        // Indexes
        builder.HasIndex(o => new { o.TenantId, o.Status })
            .HasDatabaseName("IX_Offers_TenantId_Status");

        builder.HasIndex(o => new { o.TenantId, o.CreatedAtUtc })
            .HasDatabaseName("IX_Offers_TenantId_CreatedAt");

        builder.HasIndex(o => o.ExpiresAtUtc)
            .HasDatabaseName("IX_Offers_ExpiresAtUtc");

        // Audit columns
        builder.Property(o => o.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(o => o.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(o => o.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(o => o.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);
    }
}
