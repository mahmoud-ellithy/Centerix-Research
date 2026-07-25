namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Auditing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs", "Platform");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Action)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.EntityType)
            .HasMaxLength(100);

        builder.Property(a => a.EntityId)
            .HasMaxLength(100);

        builder.Property(a => a.UserId)
            .HasMaxLength(450);

        builder.Property(a => a.IPAddress)
            .HasMaxLength(45);

        builder.Property(a => a.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(a => a.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(a => a.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(a => a.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(a => a.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(a => new { a.TenantId, a.PerformedAt });

        builder.HasIndex(a => a.UserId);

        // FK to AspNetUsers (set null on user delete — audit history must survive).
        builder.HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
