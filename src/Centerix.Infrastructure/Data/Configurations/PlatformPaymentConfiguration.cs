namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Invoicing;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PlatformPaymentConfiguration : IEntityTypeConfiguration<PlatformPayment>
{
    public void Configure(EntityTypeBuilder<PlatformPayment> builder)
    {
        builder.ToTable("PlatformPayments", "Platform");

        builder.HasKey(pp => pp.Id);

        builder.Property(pp => pp.Id)
            .HasColumnName("PlatformPaymentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(pp => pp.InvoiceId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(pp => pp.Amount)
            .HasPrecision(10, 2);

        builder.Property(pp => pp.Method)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(pp => pp.GatewayRef)
            .HasMaxLength(200);

        builder.Property(pp => pp.PaidAt)
            .IsRequired();

        builder.Property(pp => pp.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne(pp => pp.Invoice)
            .WithMany(i => i.PlatformPayments)
            .HasForeignKey(pp => pp.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(pp => pp.InvoiceId);
        builder.HasIndex(pp => pp.GatewayRef);
    }
}
