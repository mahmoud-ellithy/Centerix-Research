namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Promotions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the Promotion entity.
/// Platform-scoped catalog entity (NOT tenant-filtered).
/// </summary>
public class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTable("Promotions", "Platform");

        builder.HasKey(p => p.Id);

        // Name
        builder.Property(p => p.Name)
            .HasMaxLength(200)
            .IsRequired();

        // Code: optional, unique when provided
        builder.Property(p => p.Code)
            .HasMaxLength(50);

        // Type (enum → byte)
        builder.Property(p => p.Type)
            .HasConversion<byte>()
            .IsRequired();

        // Status (enum → byte)
        builder.Property(p => p.Status)
            .HasConversion<byte>()
            .IsRequired();

        // Plan reference (0 = all plans)
        builder.HasIndex(p => p.PlanId)
            .HasDatabaseName("IX_Promotions_PlanId");

        // Duration months (0 = any duration)
        builder.Property(p => p.DurationMonths)
            .IsRequired();

        // Date range
        builder.Property(p => p.StartsAtUtc)
            .IsRequired();

        builder.Property(p => p.EndsAtUtc)
            .IsRequired();

        // Priority
        builder.Property(p => p.Priority)
            .IsRequired();

        // Type-specific fields
        builder.Property(p => p.Percentage)
            .HasPrecision(5, 2);

        builder.Property(p => p.FixedAmount)
            .HasPrecision(18, 2);

        builder.Property(p => p.PromotionalPrice)
            .HasPrecision(18, 2);

        builder.Property(p => p.ChargedMonths);

        // Indexes for common query patterns
        builder.HasIndex(p => p.Status)
            .HasDatabaseName("IX_Promotions_Status");

        builder.HasIndex(p => new { p.Status, p.PlanId, p.DurationMonths })
            .HasDatabaseName("IX_Promotions_Status_PlanId_DurationMonths");

        builder.HasIndex(p => new { p.StartsAtUtc, p.EndsAtUtc })
            .HasDatabaseName("IX_Promotions_DateRange");

        builder.HasIndex(p => p.Priority)
            .HasDatabaseName("IX_Promotions_Priority");

        // Audit column renaming (follows existing convention)
        builder.Property(p => p.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(p => p.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(p => p.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(p => p.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);
    }
}
