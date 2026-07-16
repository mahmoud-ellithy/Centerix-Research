namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantCRMLeadConfiguration : IEntityTypeConfiguration<TenantCRMLead>
{
    public void Configure(EntityTypeBuilder<TenantCRMLead> builder)
    {
        builder.ToTable("TenantCRMLeads", "Platform");
        
        builder.HasKey(tc => tc.Id);
        
        builder.Property(tc => tc.CenterName)
            .HasMaxLength(200)
            .IsRequired();
        
        builder.Property(tc => tc.ContactName)
            .HasMaxLength(150)
            .IsRequired();
        
        builder.Property(tc => tc.Phone)
            .HasMaxLength(30)
            .IsRequired();
        
        builder.Property(tc => tc.Source)
            .HasMaxLength(50)
            .IsRequired();
        
        builder.Property(tc => tc.Stage)
            .HasMaxLength(50)
            .IsRequired();
        
        builder.Property(tc => tc.TenantId)
            .IsRequired();
    }
}
