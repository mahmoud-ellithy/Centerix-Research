namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Staff;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlatformRoleConfiguration : IEntityTypeConfiguration<PlatformRole>
{
    public void Configure(EntityTypeBuilder<PlatformRole> builder)
    {
        builder.ToTable("PlatformRoles", "Platform");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("RoleId")
            .HasColumnType("int")
            .ValueGeneratedOnAdd();

        builder.Property(r => r.Code)
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(r => r.DisplayName)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(r => r.Code)
            .IsUnique()
            .HasDatabaseName("UX_PlatformRoles_Code");
    }
}
