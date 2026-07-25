namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Students.Lookups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AcademicStageConfiguration : IEntityTypeConfiguration<AcademicStage>
{
    public void Configure(EntityTypeBuilder<AcademicStage> builder)
    {
        builder.ToTable("AcademicStages", "Platform");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("StageId")
            .ValueGeneratedOnAdd();

        builder.Property(s => s.Code)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(s => s.DisplayName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(s => s.SortOrder)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(s => s.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        // Audit columns — keep native datetimeoffset mapping (matches the rest of the platform).
        builder.Property(s => s.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(s => s.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        // Soft-delete columns are not part of the lookup table spec.
        builder.Ignore(s => s.DeletedAtUtc);
        builder.Ignore(s => s.DeletedBy);

        builder.HasIndex(s => new { s.TenantId, s.Code })
            .IsUnique()
            .HasDatabaseName("UX_AcademicStages_TenantId_Code");

        builder.HasIndex(s => new { s.TenantId, s.SortOrder });
    }
}
