namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Staff;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlatformUserConfiguration : IEntityTypeConfiguration<PlatformUser>
{
    public void Configure(EntityTypeBuilder<PlatformUser> builder)
    {
        builder.ToTable("PlatformUsers", "Platform");

        builder.HasKey(pu => pu.Id);

        builder.Property(pu => pu.Id)
            .HasColumnName("PlatformUserId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(pu => pu.Email)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(pu => pu.FullName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(pu => pu.PasswordHash)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(pu => pu.Is2FAEnabled)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(pu => pu.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.HasIndex(pu => pu.Email)
            .IsUnique()
            .HasDatabaseName("UX_PlatformUsers_Email");

        builder.HasIndex(pu => pu.IsActive);
    }
}
