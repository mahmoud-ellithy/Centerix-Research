namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Teachers.TeacherSalaryConfigs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TeacherSalaryConfigConfiguration : IEntityTypeConfiguration<TeacherSalaryConfig>
{
    public void Configure(EntityTypeBuilder<TeacherSalaryConfig> builder)
    {
        builder.ToTable("TeacherSalaryConfigs", "Platform");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("ConfigId")
            .ValueGeneratedOnAdd();

        builder.Property(c => c.TeacherId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(c => c.GroupId)
            .HasColumnType("uniqueidentifier");

        builder.Property(c => c.SalaryType)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(c => c.Value)
            .HasColumnType("decimal(8,2)")
            .IsRequired();

        builder.Property(c => c.EffectiveFrom)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(c => c.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(c => c.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(c => c.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(c => c.CreatedBy).HasColumnName("CreatedBy");
        builder.Property(c => c.LastModifiedBy).HasColumnName("ModifiedBy");

        builder.HasIndex(c => c.TenantId);
        builder.HasIndex(c => new { c.TeacherId, c.EffectiveFrom });
        builder.HasIndex(c => c.GroupId);

        builder.HasOne(c => c.Teacher)
            .WithMany()
            .HasForeignKey(c => c.TeacherId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}