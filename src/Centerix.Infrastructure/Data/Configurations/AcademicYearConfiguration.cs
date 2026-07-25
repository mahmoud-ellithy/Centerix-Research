namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Students.Lookups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AcademicYearConfiguration : IEntityTypeConfiguration<AcademicYear>
{
    public void Configure(EntityTypeBuilder<AcademicYear> builder)
    {
        builder.ToTable("AcademicYears", "Platform");

        builder.HasKey(y => y.Id);

        builder.Property(y => y.Id)
            .HasColumnName("YearId")
            .ValueGeneratedOnAdd();

        builder.Property(y => y.StageId)
            .IsRequired();

        builder.Property(y => y.YearCode)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(y => y.YearName)
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(y => y.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        // Audit columns — keep native datetimeoffset mapping (matches the rest of the platform).
        builder.Property(y => y.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(y => y.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        // Soft-delete columns are not part of the lookup table spec.
        builder.Ignore(y => y.DeletedAtUtc);
        builder.Ignore(y => y.DeletedBy);

        builder.HasOne(y => y.Stage)
            .WithMany(s => s.AcademicYears)
            .HasForeignKey(y => y.StageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(y => new { y.TenantId, y.StageId, y.YearCode })
            .IsUnique()
            .HasDatabaseName("UX_AcademicYears_TenantId_StageId_YearCode");

        builder.HasIndex(y => new { y.TenantId, y.StageId });
    }
}
