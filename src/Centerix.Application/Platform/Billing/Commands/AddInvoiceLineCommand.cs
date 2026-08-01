namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;

using MediatR;

public record AddInvoiceLineCommand(
    Guid InvoiceId,
    byte SourceType,
    Guid? SourceId,
    string Description,
    int Quantity,
    decimal UnitPrice,
    int? ProratedDays) : IRequest<Result<Created>>;

public class AddInvoiceLineHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<AddInvoiceLineCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(AddInvoiceLineCommand request, CancellationToken cancellationToken)
    {
        var invoice = await dbContext.Invoices.FindAsync([request.InvoiceId], cancellationToken: cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound("Invoice.NotFound", $"Invoice with id '{request.InvoiceId}' was not found.");
        }

        var lineTotal = request.Quantity * request.UnitPrice;

        var line = InvoiceLine.Create(
            Guid.NewGuid(),
            request.InvoiceId,
            (InvoiceLineSourceType)request.SourceType,
            request.SourceId,
            request.Description,
            request.Quantity,
            request.UnitPrice,
            request.ProratedDays,
            lineTotal);

        dbContext.InvoiceLines.Add(line);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "InvoiceLine.Create",
            entityType: nameof(InvoiceLine),
            entityId: line.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                line.InvoiceId,
                SourceType = line.SourceType.ToString(),
                line.SourceId,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.ProratedDays,
                line.LineTotal
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
