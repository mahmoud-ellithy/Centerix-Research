namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Students.Attendance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AttendanceLogConfiguration : IEntityTypeConfiguration<AttendanceLog>
{
    public void Configure(EntityTypeBuilder<AttendanceLog> builder)
    {
        builder.ToTable("AttendanceLogs", "Platform");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .HasColumnName("AttendanceId")
            .ValueGeneratedOnAdd();

        builder.Property(a => a.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(a => a.StudentId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        // GroupId stores a reference to a not-yet-implemented Groups aggregate — plain GUID,
        // no FK constraint for now. The Groups entity should introduce FK_AttendanceLogs_Groups_GroupId.
        builder.Property(a => a.GroupId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(a => a.SessionDate)
            .HasColumnType("date");

        builder.Property(a => a.Status)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(a => a.CheckInTime)
            .HasColumnType("time");

        builder.Property(a => a.IsOffline)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(a => a.SyncedAt)
            .HasColumnType("datetime2");

        builder.Property(a => a.CreatedAtUtc).HasColumnName("CreatedAt");
        builder.Property(a => a.LastModifiedUtc).HasColumnName("ModifiedAt");

        builder.Property(a => a.CreatedBy).HasColumnName("CreatedBy");
        builder.Property(a => a.LastModifiedBy).HasColumnName("ModifiedBy");

        builder.Property(a => a.RowVersion)
            .IsRowVersion();

        builder.HasOne(a => a.Student)
            .WithMany()
            .HasForeignKey(a => a.StudentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.TenantId, a.SessionDate });
        builder.HasIndex(a => new { a.StudentId, a.SessionDate })
            .IsUnique()
            .HasDatabaseName("UX_AttendanceLogs_Student_Session");
        builder.HasIndex(a => new { a.GroupId, a.SessionDate });
        builder.HasIndex(a => a.IsOffline);
    }
}
