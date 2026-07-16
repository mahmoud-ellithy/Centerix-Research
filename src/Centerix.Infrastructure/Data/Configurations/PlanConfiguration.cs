namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("Plans", "Platform");
        
        builder.HasKey(p => p.Id);
        
        builder.HasIndex(p => p.Code)
            .IsUnique();
        
        builder.Property(p => p.Code)
            .HasMaxLength(30)
            .IsRequired();
        
        builder.Property(p => p.DisplayName)
            .HasMaxLength(100)
            .IsRequired();
        
        builder.Property(p => p.MonthlyPrice)
            .HasPrecision(10, 2);
    }
}
