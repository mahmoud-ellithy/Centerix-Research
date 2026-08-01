namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;

using MediatR;

public record CreateInvoiceCommand(
    string? InvoiceNumber,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount) : IRequest<Result<Created>>;

public class CreateInvoiceHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateInvoiceCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateInvoiceCommand request,
        CancellationToken cancellationToken)
    {
        var invoiceNumber = string.IsNullOrWhiteSpace(request.InvoiceNumber)
            ? $"INV-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
            : request.InvoiceNumber;

        var invoiceResult = Invoice.Create(
            Guid.NewGuid(),
            invoiceNumber,
            request.PeriodStart,
            request.PeriodEnd,
            request.Subtotal,
            request.DiscountAmount,
            request.TaxAmount,
            request.TotalAmount);

        if (!invoiceResult.IsSuccess)
        {
            return invoiceResult.Errors!;
        }

        dbContext.Invoices.Add(invoiceResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Invoice.Create",
            entityType: nameof(Invoice),
            entityId: invoiceResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                invoiceResult.Value.InvoiceNumber,
                invoiceResult.Value.PeriodStart,
                invoiceResult.Value.PeriodEnd,
                invoiceResult.Value.Subtotal,
                invoiceResult.Value.DiscountAmount,
                invoiceResult.Value.TaxAmount,
                invoiceResult.Value.TotalAmount,
                Status = invoiceResult.Value.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
