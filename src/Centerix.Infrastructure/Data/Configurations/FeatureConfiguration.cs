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
    }
}
