namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class FeatureConfiguration : IEntityTypeConfiguration<Feature>
{
    public void Configure(EntityTypeBuilder<Feature> builder)
    {
        builder.ToTable("Features", "Platform");

        builder.HasKey(f => f.Id);

        builder.HasIndex(f => f.Code)
            .IsUnique();

        builder.Property(f => f.Code)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(f => f.Description)
            .HasMaxLength(500);

        builder.Property(f => f.Module)
            .HasMaxLength(50);

        builder.Property(f => f.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(f => f.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(f => f.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(f => f.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);
    }
}
