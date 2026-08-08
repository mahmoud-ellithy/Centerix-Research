namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Credits;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantCreditConfiguration : IEntityTypeConfiguration<TenantCredit>
{
    public void Configure(EntityTypeBuilder<TenantCredit> builder)
    {
        builder.ToTable("TenantCredits", "Platform");

        builder.HasKey(tc => tc.Id);

        builder.Property(tc => tc.Id)
            .HasColumnName("TenantCreditId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(tc => tc.Amount)
            .HasPrecision(10, 2);

        builder.Property(tc => tc.SourceType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(tc => tc.SourceId)
            .HasColumnType("uniqueidentifier");

        builder.Property(tc => tc.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(tc => tc.AppliedToInvoiceLineId)
            .HasColumnType("uniqueidentifier");

        builder.Property(tc => tc.ReversalOfCreditId)
            .HasColumnType("uniqueidentifier");

        builder.Property(tc => tc.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(tc => tc.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(tc => tc.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(tc => tc.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(tc => tc.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(tc => tc.TenantId);
        builder.HasIndex(tc => new { tc.TenantId, tc.Status });
    }
}
