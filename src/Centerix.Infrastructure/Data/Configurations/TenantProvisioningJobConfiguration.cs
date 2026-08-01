namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Operations;
using Centerix.Domain.Platform.Operations.Enums;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantProvisioningJobConfiguration : IEntityTypeConfiguration<TenantProvisioningJob>
{
    public void Configure(EntityTypeBuilder<TenantProvisioningJob> builder)
    {
        builder.ToTable("TenantProvisioningJobs", "Platform");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Status)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(j => j.ErrorMessage)
            .HasMaxLength(2000);

        builder.Property(j => j.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(j => j.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(j => j.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(j => j.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(j => j.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(j => j.TenantId);
        builder.HasIndex(j => j.Status);
    }
}
