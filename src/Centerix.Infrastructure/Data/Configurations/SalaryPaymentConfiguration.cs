namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Teachers.SalaryPayments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class SalaryPaymentConfiguration : IEntityTypeConfiguration<SalaryPayment>
{
    public void Configure(EntityTypeBuilder<SalaryPayment> builder)
    {
        builder.ToTable("SalaryPayments", "Platform");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("PaymentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(p => p.TeacherId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(p => p.PeriodMonth)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(p => p.PeriodYear)
            .HasColumnType("smallint")
            .IsRequired();

        builder.Property(p => p.GrossAmount)
            .HasColumnType("decimal(10,2)")
            .IsRequired();

        builder.Property(p => p.NetAmount)
            .HasColumnType("decimal(10,2)")
            .IsRequired();

        builder.Property(p => p.Status)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(p => p.PaidAt)
            .HasColumnType("datetime2");

        builder.Property(p => p.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(p => p.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(p => p.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(p => p.CreatedBy).HasColumnName("CreatedBy");
        builder.Property(p => p.LastModifiedBy).HasColumnName("ModifiedBy");

        builder.HasIndex(p => new { p.TeacherId, p.PeriodYear, p.PeriodMonth })
            .IsUnique()
            .HasDatabaseName("UX_SalaryPayments_Teacher_Period");
        builder.HasIndex(p => new { p.TenantId, p.PeriodYear, p.PeriodMonth });
        builder.HasIndex(p => new { p.TenantId, p.Status });

        builder.HasOne(p => p.Teacher)
            .WithMany()
            .HasForeignKey(p => p.TeacherId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}