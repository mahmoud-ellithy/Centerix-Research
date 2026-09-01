namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Teachers.TeacherRatings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TeacherRatingConfiguration : IEntityTypeConfiguration<TeacherRating>
{
    public void Configure(EntityTypeBuilder<TeacherRating> builder)
    {
        builder.ToTable("TeacherRatings", "Platform");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("RatingId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(r => r.TeacherId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(r => r.StudentId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(r => r.GroupId)
            .HasColumnType("uniqueidentifier");

        builder.Property(r => r.Stars)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(r => r.Comment)
            .HasMaxLength(500);

        builder.Property(r => r.PeriodMonth)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(r => r.PeriodYear)
            .HasColumnType("smallint")
            .IsRequired();

        builder.Property(r => r.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(r => r.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(r => r.LastModifiedUtc).HasColumnName("ModifiedAt");
        builder.Property(r => r.CreatedBy).HasColumnName("CreatedBy");
        builder.Property(r => r.LastModifiedBy).HasColumnName("ModifiedBy");

        builder.HasIndex(r => new { r.TenantId, r.TeacherId, r.PeriodYear, r.PeriodMonth });
        builder.HasIndex(r => new { r.TenantId, r.StudentId });
        builder.HasIndex(r => r.GroupId);

        builder.HasOne(r => r.Teacher)
            .WithMany()
            .HasForeignKey(r => r.TeacherId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Student)
            .WithMany()
            .HasForeignKey(r => r.StudentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}