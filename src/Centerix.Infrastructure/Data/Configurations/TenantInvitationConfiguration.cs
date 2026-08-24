namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantInvitationConfiguration : IEntityTypeConfiguration<TenantInvitation>
{
    public void Configure(EntityTypeBuilder<TenantInvitation> builder)
    {
        builder.ToTable("TenantInvitations", "Platform");

        builder.HasKey(ti => ti.Id);

        builder.Property(ti => ti.Id)
            .HasColumnName("Id")
            .HasColumnType("uniqueidentifier");

        builder.Property(ti => ti.TenantId)
            .HasColumnName("TenantId")
            .HasColumnType("nvarchar(64)")
            .IsRequired();

        builder.Property(ti => ti.Email)
            .HasColumnName("Email")
            .HasColumnType("nvarchar(256)")
            .IsRequired();

        builder.Property(ti => ti.NormalizedEmail)
            .HasColumnName("NormalizedEmail")
            .HasColumnType("nvarchar(256)")
            .IsRequired();

        builder.Property(ti => ti.InvitedByUserId)
            .HasColumnName("InvitedByUserId")
            .HasColumnType("nvarchar(450)")
            .IsRequired();

        builder.Property(ti => ti.RoleName)
            .HasColumnName("RoleName")
            .HasColumnType("nvarchar(128)")
            .IsRequired();

        builder.Property(ti => ti.TokenHash)
            .HasColumnName("TokenHash")
            .HasColumnType("nvarchar(128)")
            .IsRequired();

        builder.Property(ti => ti.ExpiresAtUtc)
            .HasColumnName("ExpiresAtUtc")
            .HasColumnType("datetimeoffset")
            .IsRequired();

        builder.Property(ti => ti.Status)
            .HasColumnType("tinyint")
            .IsRequired()
            .HasDefaultValue(InvitationStatus.Pending);

        builder.Property(ti => ti.CreatedAtUtc)
            .HasColumnName("CreatedAtUtc")
            .HasColumnType("datetimeoffset")
            .IsRequired();

        builder.Property(ti => ti.AcceptedAtUtc)
            .HasColumnName("AcceptedAtUtc")
            .HasColumnType("datetimeoffset");

        builder.Property(ti => ti.AcceptedByUserId)
            .HasColumnName("AcceptedByUserId")
            .HasColumnType("nvarchar(450)");

        builder.Property(ti => ti.RevokedAtUtc)
            .HasColumnName("RevokedAtUtc")
            .HasColumnType("datetimeoffset");

        builder.Property(ti => ti.RevokedByUserId)
            .HasColumnName("RevokedByUserId")
            .HasColumnType("nvarchar(450)");

        // Indexes for common query patterns
        builder.HasIndex(ti => ti.TenantId)
            .HasDatabaseName("IX_TenantInvitations_TenantId");

        builder.HasIndex(ti => ti.NormalizedEmail)
            .HasDatabaseName("IX_TenantInvitations_NormalizedEmail");

        builder.HasIndex(ti => ti.TokenHash)
            .HasDatabaseName("IX_TenantInvitations_TokenHash")
            .IsUnique();

        builder.HasIndex(ti => ti.Status)
            .HasDatabaseName("IX_TenantInvitations_Status");

        // Composite index for duplicate invitation check: (TenantId, NormalizedEmail, Status)
        builder.HasIndex(ti => new { ti.TenantId, ti.NormalizedEmail, ti.Status })
            .HasDatabaseName("IX_TenantInvitations_Tenant_Email_Status");

        // FK to IdentityUser (inviter)
        builder.HasOne<Microsoft.AspNetCore.Identity.IdentityUser>()
            .WithMany()
            .HasForeignKey(ti => ti.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK to IdentityUser (acceptor, nullable)
        builder.HasOne<Microsoft.AspNetCore.Identity.IdentityUser>()
            .WithMany()
            .HasForeignKey(ti => ti.AcceptedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK to IdentityUser (revoker, nullable)
        builder.HasOne<Microsoft.AspNetCore.Identity.IdentityUser>()
            .WithMany()
            .HasForeignKey(ti => ti.RevokedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
