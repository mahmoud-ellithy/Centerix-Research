namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlatformAuditLogConfiguration : IEntityTypeConfiguration<PlatformAuditLog>
{
    public void Configure(EntityTypeBuilder<PlatformAuditLog> builder)
    {
        builder.ToTable("PlatformAuditLog", "Platform");
        
        builder.HasKey(pa => pa.Id);
        
        builder.Property(pa => pa.Action)
            .HasMaxLength(100)
            .IsRequired();
        
        builder.Property(pa => pa.EntityType)
            .HasMaxLength(100);
        
        builder.Property(pa => pa.EntityId)
            .HasMaxLength(100);
        
        builder.Property(pa => pa.IPAddress)
            .HasMaxLength(45);
        
        builder.Property(pa => pa.TenantId)
            .IsRequired();
    }
}
