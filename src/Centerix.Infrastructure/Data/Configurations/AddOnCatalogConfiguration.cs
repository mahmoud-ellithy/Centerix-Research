namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions.AddOns;
using Centerix.Domain.Platform.Subscriptions.AddOns.Enums;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AddOnCatalogConfiguration : IEntityTypeConfiguration<AddOnCatalog>
{
    public void Configure(EntityTypeBuilder<AddOnCatalog> builder)
    {
        builder.ToTable("AddOnCatalogs", "Platform");

        builder.HasKey(a => a.Id);

        builder.HasIndex(a => a.Code)
            .IsUnique();

        builder.Property(a => a.Code)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(a => a.DisplayName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.UnitType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(a => a.BillingType)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(a => a.IsActive)
            .HasDefaultValue(true)
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
    }
}
