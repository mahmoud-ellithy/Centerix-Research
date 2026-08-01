namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Staff;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlatformPermissionConfiguration : IEntityTypeConfiguration<PlatformPermission>
{
    public void Configure(EntityTypeBuilder<PlatformPermission> builder)
    {
        builder.ToTable("PlatformPermissions", "Platform");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("PermissionId")
            .HasColumnType("int")
            .ValueGeneratedOnAdd();

        builder.Property(p => p.Module)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.Action)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(p => p.Code)
            .HasMaxLength(80)
            .IsRequired();

        builder.HasIndex(p => p.Code)
            .IsUnique()
            .HasDatabaseName("UX_PlatformPermissions_Code");
    }
}
