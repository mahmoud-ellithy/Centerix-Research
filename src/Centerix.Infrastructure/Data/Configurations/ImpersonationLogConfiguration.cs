namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Staff;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ImpersonationLogConfiguration : IEntityTypeConfiguration<ImpersonationLog>
{
    public void Configure(EntityTypeBuilder<ImpersonationLog> builder)
    {
        builder.ToTable("ImpersonationLogs", "Platform");

        builder.HasKey(il => il.Id);

        builder.Property(il => il.Id)
            .HasColumnName("LogId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(il => il.PlatformUserId)
            .HasColumnName("PlatformUserId")
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(il => il.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(il => il.TargetUserId)
            .HasColumnName("TargetUserId")
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(il => il.Reason)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(il => il.IPAddress)
            .HasMaxLength(45)
            .IsRequired();

        builder.HasOne(il => il.PlatformUser)
            .WithMany()
            .HasForeignKey(il => il.PlatformUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(il => il.PlatformUserId);
        builder.HasIndex(il => il.TenantId);
        builder.HasIndex(il => il.StartedAt);
    }
}
