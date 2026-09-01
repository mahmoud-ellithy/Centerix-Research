namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Teachers.Teachers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TeacherConfiguration : IEntityTypeConfiguration<Teacher>
{
    public void Configure(EntityTypeBuilder<Teacher> builder)
    {
        builder.ToTable("Teachers", "Platform");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("TeacherId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(t => t.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(t => t.FullName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.Phone)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(t => t.Qualification)
            .HasMaxLength(200);

        builder.Property(t => t.YearsExp)
            .HasColumnType("tinyint");

        builder.Property(t => t.Status)
            .HasConversion<string>()
            .HasMaxLength(15)
            .IsRequired();

        builder.Property(t => t.JoinedAt)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(t => t.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(t => t.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(t => t.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(t => t.DeletedAtUtc).HasColumnName("DeletedAt");
        builder.Property(t => t.CreatedBy).HasColumnName("CreatedBy");
        builder.Property(t => t.LastModifiedBy).HasColumnName("ModifiedBy");
        builder.Property(t => t.DeletedBy).HasColumnName("DeletedBy");

        builder.Property(t => t.RowVersion)
            .IsRowVersion();

        builder.HasQueryFilter(t => t.DeletedAtUtc == null);

        builder.HasIndex(t => t.TenantId);
        builder.HasIndex(t => new { t.TenantId, t.UserId })
            .IsUnique()
            .HasDatabaseName("UX_Teachers_TenantId_UserId");
        builder.HasIndex(t => new { t.TenantId, t.BranchId });
        builder.HasIndex(t => new { t.TenantId, t.Status });

        builder.HasOne(t => t.Branch)
            .WithMany()
            .HasForeignKey(t => t.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}