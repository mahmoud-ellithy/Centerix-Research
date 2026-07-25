namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions", "Platform");

        builder.HasKey(p => p.Id);

        builder.HasIndex(p => p.Code)
            .IsUnique();

        builder.Property(p => p.Module)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.Action)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.Code)
            .HasMaxLength(80)
            .IsRequired();

        builder.Property(p => p.Description)
            .HasMaxLength(200);

        builder.HasMany(p => p.RolePermissions)
            .WithOne(rp => rp.Permission)
            .HasForeignKey(rp => rp.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
