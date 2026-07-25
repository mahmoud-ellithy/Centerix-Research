namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", "Platform");

        builder.HasKey(rt => rt.Id);

        builder.Property(rt => rt.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(rt => rt.TokenHash)
            .HasMaxLength(128) // SHA-256 hex = 64 chars; leave headroom for future schemes
            .IsRequired();

        builder.Property(rt => rt.DeviceInfo)
            .HasMaxLength(300);

        builder.Property(rt => rt.IPAddress)
            .HasMaxLength(45);

        builder.Property(rt => rt.TenantId)
            .IsRequired();

        // Lookups by hash (rotation, validation) and by user (revoke-all, sessions list).
        builder.HasIndex(rt => rt.TokenHash)
            .IsUnique();

        builder.HasIndex(rt => new { rt.UserId, rt.ExpiresAtUtc });

        builder.HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
