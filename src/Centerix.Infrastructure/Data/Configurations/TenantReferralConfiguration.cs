namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Referrals;
using Centerix.Domain.Platform.Referrals.Enums;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantReferralConfiguration : IEntityTypeConfiguration<TenantReferral>
{
    public void Configure(EntityTypeBuilder<TenantReferral> builder)
    {
        builder.ToTable("TenantReferrals", "Platform");

        builder.HasKey(r => r.Id);

        builder.HasIndex(r => r.ReferredTenantId)
            .IsUnique();

        builder.HasOne(r => r.TenantReferralCode)
            .WithMany()
            .HasForeignKey(r => r.ReferralCodeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(r => r.ReferrerTenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(r => r.ReferredTenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(r => r.Status)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(r => r.RewardType)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(r => r.RewardValue)
            .HasPrecision(10, 2);

        builder.Property(r => r.RewardAppliedTo)
            .HasMaxLength(450);

        builder.Property(r => r.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(r => r.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(r => r.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(r => r.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(r => r.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(r => r.TenantId);
        builder.HasIndex(r => r.ReferrerTenantId);
        builder.HasIndex(r => r.ReferralCodeId);
    }
}
