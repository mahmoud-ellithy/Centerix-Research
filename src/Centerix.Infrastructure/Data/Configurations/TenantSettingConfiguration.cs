namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Operations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantSettingConfiguration : IEntityTypeConfiguration<TenantSetting>
{
    public void Configure(EntityTypeBuilder<TenantSetting> builder)
    {
        builder.ToTable("TenantSettings", "Platform");

        builder.HasKey(s => s.Id);

        builder.HasIndex(s => new { s.TenantId, s.Key })
            .IsUnique();

        builder.Property(s => s.Key)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(s => s.Value)
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(s => s.ValueType)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(s => s.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(s => s.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(s => s.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(s => s.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(s => s.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(s => s.TenantId);
    }
}
