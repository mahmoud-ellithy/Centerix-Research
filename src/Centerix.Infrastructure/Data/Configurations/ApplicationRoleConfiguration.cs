namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.ToTable("AspNetRoles");

        builder.Property(r => r.Code)
            .HasMaxLength(100);

        builder.Property(r => r.DisplayName)
            .HasMaxLength(150);

        builder.Property(r => r.IsSystem)
            .HasDefaultValue(false);

        builder.HasIndex(r => r.Code)
            .IsUnique()
            .HasFilter("[Code] IS NOT NULL");
    }
}
