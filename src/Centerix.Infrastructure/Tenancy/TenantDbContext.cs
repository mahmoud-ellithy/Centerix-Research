using Finbuckle.MultiTenant.EntityFrameworkCore.Stores.EFCoreStore;
using Microsoft.EntityFrameworkCore;

namespace Centerix.Infrastructure.Tenancy;

public class TenantDbContext(DbContextOptions<TenantDbContext> options) : EFCoreStoreDbContext<CenterixTenantInfo>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CenterixTenantInfo>()
            .ToTable("Tenants", "Platform");

        // Configure missing ERD fields with lengths and indexes
        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.Slug)
            .HasMaxLength(60);

        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.Subdomain)
            .HasMaxLength(100);
        
        modelBuilder.Entity<CenterixTenantInfo>()
            .HasIndex(t => t.Subdomain)
            .IsUnique();

        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.DisplayName)
            .HasMaxLength(200);

        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.LogoUrl)
            .HasMaxLength(500);

        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.PrimaryColor)
            .HasMaxLength(7);

        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.Country)
            .HasMaxLength(2);

        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.Currency)
            .HasMaxLength(3);

        modelBuilder.Entity<CenterixTenantInfo>()
            .Property(t => t.Timezone)
            .HasMaxLength(60);
    }
}