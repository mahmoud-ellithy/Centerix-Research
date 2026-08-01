namespace Centerix.Domain.Platform.Billing.Invoicing;

using Centerix.Domain.Common;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;

public class InvoiceLine : Entity
{
    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public InvoiceLineSourceType SourceType { get; private set; }
    public Guid? SourceId { get; private set; }
    public string Description { get; private set; } = default!;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public int? ProratedDays { get; private set; }
    public decimal LineTotal { get; private set; }

    public Invoice Invoice { get; private set; } = default!;

    private InvoiceLine() { }

    private InvoiceLine(
        Guid id,
        Guid invoiceId,
        InvoiceLineSourceType sourceType,
        Guid? sourceId,
        string description,
        int quantity,
        decimal unitPrice,
        int? proratedDays,
        decimal lineTotal)
    {
        Id = id;
        InvoiceId = invoiceId;
        SourceType = sourceType;
        SourceId = sourceId;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        ProratedDays = proratedDays;
        LineTotal = lineTotal;
    }

    public static InvoiceLine Create(
        Guid id,
        Guid invoiceId,
        InvoiceLineSourceType sourceType,
        Guid? sourceId,
        string description,
        int quantity,
        decimal unitPrice,
        int? proratedDays,
        decimal lineTotal)
    {
        return new InvoiceLine(id, invoiceId, sourceType, sourceId, description, quantity, unitPrice, proratedDays, lineTotal);
    }
}
