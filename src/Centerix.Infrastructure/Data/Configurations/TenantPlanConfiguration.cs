namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TenantPlanConfiguration : IEntityTypeConfiguration<TenantPlan>
{
    public void Configure(EntityTypeBuilder<TenantPlan> builder)
    {
        builder.ToTable("TenantPlans", "Platform");
        
        builder.HasKey(tp => tp.Id);
        
        builder.HasOne(tp => tp.Plan)
            .WithMany(p => p.TenantPlans)
            .HasForeignKey(tp => tp.PlanId)
            .OnDelete(DeleteBehavior.Restrict);
        
        builder.Property(tp => tp.TenantId)
            .IsRequired();
    }
}
