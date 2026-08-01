namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Operations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantSchemaVersionConfiguration : IEntityTypeConfiguration<TenantSchemaVersion>
{
    public void Configure(EntityTypeBuilder<TenantSchemaVersion> builder)
    {
        builder.ToTable("TenantSchemaVersions", "Platform");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("TenantId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(s => s.CurrentVersion)
            .HasMaxLength(30)
            .IsRequired();
    }
}
