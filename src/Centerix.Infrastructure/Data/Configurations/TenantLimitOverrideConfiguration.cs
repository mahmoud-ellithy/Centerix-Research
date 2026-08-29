namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions.LimitOverrides;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantLimitOverrideConfiguration : IEntityTypeConfiguration<TenantLimitOverride>
{
    public void Configure(EntityTypeBuilder<TenantLimitOverride> builder)
    {
        builder.ToTable("TenantLimitOverrides", "Platform");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.LimitType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(o => o.Reason)
            .HasMaxLength(500);

        builder.Property(o => o.TenantId)
            .HasMaxLength(450)
            .IsRequired();

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

        builder.HasIndex(o => o.TenantId);

        // One override per (tenant, limit type) — overrides REPLACE plan limits, so duplicates
        // would be ambiguous. Enforced in the database.
        builder.HasIndex(o => new { o.TenantId, o.LimitType })
            .IsUnique()
            .HasDatabaseName("UX_TenantLimitOverrides_TenantId_LimitType");
    }
}
