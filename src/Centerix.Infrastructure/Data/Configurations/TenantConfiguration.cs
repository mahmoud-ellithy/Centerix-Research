namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants", "Platform");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("TenantId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(t => t.Slug)
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(t => t.Subdomain)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(t => t.DisplayName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.LogoUrl)
            .HasMaxLength(500);

        builder.Property(t => t.PrimaryColor)
            .HasMaxLength(7)
            .HasColumnType("nchar(7)");

        builder.Property(t => t.Country)
            .HasMaxLength(2)
            .HasColumnType("nchar(2)")
            .IsRequired();

        builder.Property(t => t.Currency)
            .HasMaxLength(3)
            .HasColumnType("nchar(3)")
            .IsRequired();

        builder.Property(t => t.Timezone)
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(t => t.OwnerFirstName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(t => t.OwnerLastName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(t => t.OwnerEmail)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.OwnerPhone)
            .HasMaxLength(30);

        builder.Property(t => t.IsolationMode)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(t => t.DatabaseServer)
            .HasMaxLength(200);

        builder.Property(t => t.ConnectionStringRef)
            .HasMaxLength(200);

        builder.Property(t => t.CurrentPlanId)
            .HasColumnType("int");

        builder.Property(t => t.LifecycleStatus)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(t => t.SuspendedReason)
            .HasMaxLength(200);

        builder.Property(t => t.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.CreatedAtUtc)
            .HasColumnName("CreatedAt");

        builder.Property(t => t.LastModifiedUtc)
            .HasColumnName("ModifiedAt");

        builder.Property(t => t.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasMaxLength(450);

        builder.Property(t => t.LastModifiedBy)
            .HasColumnName("ModifiedBy")
            .HasMaxLength(450);

        builder.HasIndex(t => t.Slug)
            .IsUnique()
            .HasDatabaseName("UX_Tenants_Slug");

        builder.HasIndex(t => t.Subdomain)
            .IsUnique()
            .HasDatabaseName("UX_Tenants_Subdomain");

        builder.HasIndex(t => t.CurrentPlanId);
        builder.HasIndex(t => t.IsActive);
        builder.HasIndex(t => t.LifecycleStatus);
    }
}
