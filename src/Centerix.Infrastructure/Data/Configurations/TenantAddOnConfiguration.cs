namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions.AddOns;
using Centerix.Domain.Platform.Subscriptions.AddOns.Enums;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantAddOnConfiguration : IEntityTypeConfiguration<TenantAddOn>
{
    public void Configure(EntityTypeBuilder<TenantAddOn> builder)
    {
        builder.ToTable("TenantAddOns", "Platform");

        builder.HasKey(a => a.Id);

        builder.HasOne(a => a.AddOnCatalog)
            .WithMany(c => c.TenantAddOns)
            .HasForeignKey(a => a.AddOnCatalogId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(a => a.SnapshotUnitPrice)
            .HasPrecision(10, 2);

        builder.Property(a => a.Status)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(a => a.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(a => a.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(a => a.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(a => a.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(a => a.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(a => a.TenantId);
        builder.HasIndex(a => a.AddOnCatalogId);
    }
}
