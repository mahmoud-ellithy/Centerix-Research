namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Referrals;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantReferralCodeConfiguration : IEntityTypeConfiguration<TenantReferralCode>
{
    public void Configure(EntityTypeBuilder<TenantReferralCode> builder)
    {
        builder.ToTable("TenantReferralCodes", "Platform");

        builder.HasKey(r => r.Id);

        builder.HasIndex(r => r.Code)
            .IsUnique();

        builder.Property(r => r.Code)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(r => r.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

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
    }
}
