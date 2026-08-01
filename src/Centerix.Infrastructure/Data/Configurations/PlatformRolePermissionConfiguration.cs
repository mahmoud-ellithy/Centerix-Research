namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Staff;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlatformRolePermissionConfiguration : IEntityTypeConfiguration<PlatformRolePermission>
{
    public void Configure(EntityTypeBuilder<PlatformRolePermission> builder)
    {
        builder.ToTable("PlatformRolePermissions", "Platform");

        builder.HasKey(prp => new { prp.RoleId, prp.PermissionId });

        builder.Property(prp => prp.RoleId)
            .HasColumnName("RoleId")
            .HasColumnType("int");

        builder.Property(prp => prp.PermissionId)
            .HasColumnName("PermissionId")
            .HasColumnType("int");

        builder.HasOne(prp => prp.Role)
            .WithMany(r => r.RolePermissions)
            .HasForeignKey(prp => prp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(prp => prp.Permission)
            .WithMany(p => p.RolePermissions)
            .HasForeignKey(prp => prp.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
