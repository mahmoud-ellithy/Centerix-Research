namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class SubscriptionPolicyConfiguration : IEntityTypeConfiguration<SubscriptionPolicy>
{
    public void Configure(EntityTypeBuilder<SubscriptionPolicy> builder)
    {
        builder.ToTable("SubscriptionPolicies", "Platform");

        builder.HasKey(sp => sp.Id);

        builder.Property(sp => sp.GracePeriodDays)
            .IsRequired();

        builder.Property(sp => sp.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(sp => sp.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(sp => sp.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
        builder.Property(sp => sp.LastModifiedBy).HasColumnName("ModifiedBy").HasMaxLength(450);
    }
}
