namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("TenantMemberships", "Platform");

        // Unique constraint on (UserId, TenantId): a user may belong to many tenants,
        // but only once per tenant. Composite PK also satisfies the uniqueness requirement.
        builder.HasKey(tm => new { tm.UserId, tm.TenantId });

        builder.Property(tm => tm.UserId)
            .HasColumnName("UserId")
            .HasColumnType("nvarchar(450)")
            .IsRequired();

        // Must match Platform.TenantRegistry.Id (nvarchar(64)) for the cross-context FK added in the migration.
        builder.Property(tm => tm.TenantId)
            .HasColumnName("TenantId")
            .HasColumnType("nvarchar(64)")
            .IsRequired();

        builder.Property(tm => tm.Status)
            .HasColumnType("tinyint")
            .IsRequired()
            .HasDefaultValue(TenantMembershipStatus.Active);

        builder.Property(tm => tm.JoinedAtUtc)
            .HasColumnName("JoinedAtUtc")
            .HasColumnType("datetimeoffset")
            .IsRequired();

        // User side: same-context relationship to AspNetUsers. Cascade so removing a user
        // removes their memberships. Mirrors RefreshTokenConfiguration's unidirectional FK.
        builder.HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(tm => tm.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tenant side: CenterixTenantInfo lives in a SEPARATE DbContext (TenantDbContext /
        // Platform.TenantRegistry), so EF cannot model the relationship here. The FK to
        // Platform.TenantRegistry(Id) is created via raw SQL in the migration with
        // ON DELETE NO ACTION, so a tenant record cannot be hard-deleted while memberships
        // reference it (forcing explicit cleanup first).

        // Indexes for the two primary access paths:
        //  - "which tenants does this user belong to?" (UserId is the leading PK column, already indexed)
        //  - "which users belong to this tenant?" (TenantId lookup)
        builder.HasIndex(tm => tm.TenantId)
            .HasDatabaseName("IX_TenantMemberships_TenantId");

        builder.HasIndex(tm => tm.Status)
            .HasDatabaseName("IX_TenantMemberships_Status");
    }
}
