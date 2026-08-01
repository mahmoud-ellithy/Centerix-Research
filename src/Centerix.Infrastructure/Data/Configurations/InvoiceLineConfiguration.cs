namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Billing.Invoicing;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("InvoiceLines", "Platform");

        builder.HasKey(il => il.Id);

        builder.Property(il => il.Id)
            .HasColumnName("InvoiceLineId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever();

        builder.Property(il => il.InvoiceId)
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(il => il.SourceType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(il => il.SourceId)
            .HasColumnType("uniqueidentifier");

        builder.Property(il => il.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(il => il.Quantity)
            .IsRequired();

        builder.Property(il => il.UnitPrice)
            .HasPrecision(10, 2);

        builder.Property(il => il.ProratedDays);

        builder.Property(il => il.LineTotal)
            .HasPrecision(10, 2);

        builder.HasOne(il => il.Invoice)
            .WithMany(i => i.InvoiceLines)
            .HasForeignKey(il => il.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(il => il.InvoiceId);
    }
}
