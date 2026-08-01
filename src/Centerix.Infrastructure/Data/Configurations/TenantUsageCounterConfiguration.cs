namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions.UsageCounters;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantUsageCounterConfiguration : IEntityTypeConfiguration<TenantUsageCounter>
{
    public void Configure(EntityTypeBuilder<TenantUsageCounter> builder)
    {
        builder.ToTable("TenantUsageCounters", "Platform");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("TenantId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(c => c.SyncStatus)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(c => c.StorageUsedMB)
            .HasColumnType("int")
            .IsRequired();

        builder.Property(c => c.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(c => c.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(c => c.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(c => c.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(c => c.Id)
            .IsUnique();
    }
}
