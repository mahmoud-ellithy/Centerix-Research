namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Students;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> builder)
    {
        builder.ToTable("Students", "Platform");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("StudentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(s => s.FullNameAr)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(s => s.FullNameEn)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(s => s.DateOfBirth)
            .HasColumnType("date");

        builder.Property(s => s.Gender)
            .HasConversion<byte>()
            .HasColumnType("nchar(1)")
            .IsRequired();

        builder.Property(s => s.Phone)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(s => s.QRCode)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(s => s.DiscountType)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(s => s.DiscountValue)
            .HasColumnType("decimal(18,2)");

        builder.Property(s => s.Status)
            .HasConversion<string>()
            .HasMaxLength(15)
            .IsRequired();

        builder.Property(s => s.EnrolledAt)
            .HasColumnType("date");

        // Audit columns — keep native datetimeoffset mapping (matches the rest of the platform).
        builder.Property(s => s.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(s => s.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(s => s.DeletedAtUtc).HasColumnName("DeletedAt");

        builder.Property(s => s.RowVersion)
            .IsRowVersion();

        // Global query filter: hide soft-deleted rows automatically.
        builder.HasQueryFilter(s => s.DeletedAtUtc == null);

        // Indexes
        builder.HasIndex(s => s.TenantId);
        builder.HasIndex(s => s.QRCode)
            .IsUnique()
            .HasDatabaseName("UX_Students_QRCode");

        builder.HasIndex(s => new { s.TenantId, s.BranchId });
        builder.HasIndex(s => new { s.TenantId, s.StageId, s.YearId });
        builder.HasIndex(s => new { s.TenantId, s.Status });

        builder.HasOne(s => s.Branch)
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.Stage)
            .WithMany()
            .HasForeignKey(s => s.StageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.Year)
            .WithMany()
            .HasForeignKey(s => s.YearId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
