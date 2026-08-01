namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Staff;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlatformUserRoleConfiguration : IEntityTypeConfiguration<PlatformUserRole>
{
    public void Configure(EntityTypeBuilder<PlatformUserRole> builder)
    {
        builder.ToTable("PlatformUserRoles", "Platform");

        builder.HasKey(pur => new { pur.PlatformUserId, pur.RoleId });

        builder.Property(pur => pur.PlatformUserId)
            .HasColumnName("PlatformUserId")
            .HasColumnType("uniqueidentifier");

        builder.Property(pur => pur.RoleId)
            .HasColumnName("RoleId")
            .HasColumnType("int");

        builder.HasOne(pur => pur.PlatformUser)
            .WithMany(pu => pu.UserRoles)
            .HasForeignKey(pur => pur.PlatformUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pur => pur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(pur => pur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(pur => pur.RoleId);
    }
}
