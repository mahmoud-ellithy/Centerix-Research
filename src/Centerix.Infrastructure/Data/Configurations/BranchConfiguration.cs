namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Students.Branches;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches", "Platform");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Id)
            .HasColumnName("BranchId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(b => b.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(b => b.Address)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(b => b.Phone)
            .HasMaxLength(30)
            .IsRequired();

        // ManagerId is a logical FK reference to AspNetUsers.Id. We don't enforce it as a
        // database constraint because AspNetUsers.Id is nvarchar(450) while ManagerId is a
        // Guid-shaped value (we may eventually migrate to IdentityUser<Guid>).
        builder.Property(b => b.ManagerId)
            .HasColumnType("uniqueidentifier");

        builder.Property(b => b.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(b => b.TenantId)
            .HasMaxLength(450)
            .IsRequired();

        // Audit columns — keep native datetimeoffset mapping (matches the rest of the platform).
        builder.Property(b => b.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(b => b.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(b => b.DeletedAtUtc)
            .HasColumnName("DeletedAt");

        // Global query filter: hide soft-deleted rows automatically.
        builder.HasQueryFilter(b => b.DeletedAtUtc == null);

        builder.HasIndex(b => b.TenantId);
        builder.HasIndex(b => new { b.TenantId, b.IsActive });
    }
}
