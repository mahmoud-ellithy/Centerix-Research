namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Teachers.Subjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class SubjectConfiguration : IEntityTypeConfiguration<Subject>
{
    public void Configure(EntityTypeBuilder<Subject> builder)
    {
        builder.ToTable("Subjects", "Platform");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("SubjectId")
            .ValueGeneratedOnAdd();

        builder.Property(s => s.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(s => s.StageId)
            .HasColumnType("int")
            .IsRequired();

        builder.Property(s => s.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(s => s.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(s => s.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(s => s.CreatedBy).HasColumnName("CreatedBy");
        builder.Property(s => s.LastModifiedBy).HasColumnName("ModifiedBy");

        builder.HasIndex(s => new { s.TenantId, s.StageId, s.Name })
            .IsUnique()
            .HasDatabaseName("UX_Subjects_TenantId_StageId_Name");

        builder.HasIndex(s => new { s.TenantId, s.StageId });
    }
}