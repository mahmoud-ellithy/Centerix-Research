namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions", "Platform");

        builder.HasKey(rp => new { rp.RoleId, rp.PermissionId });

        builder.Property(rp => rp.RoleId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(rp => rp.PermissionId)
            .IsRequired();

        builder.HasIndex(rp => rp.PermissionId);
    }
}
