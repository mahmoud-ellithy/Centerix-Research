namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record RemoveInvoiceLineCommand(Guid InvoiceId, Guid LineId) : IRequest<Result<Deleted>>;

public class RemoveInvoiceLineHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<RemoveInvoiceLineCommand, Result<Deleted>>
{
    public async Task<Result<Deleted>> Handle(RemoveInvoiceLineCommand request, CancellationToken cancellationToken)
    {
        var invoice = await dbContext.Invoices.FindAsync([request.InvoiceId], cancellationToken: cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound("Invoice.NotFound", $"Invoice with id '{request.InvoiceId}' was not found.");
        }

        var line = await dbContext.InvoiceLines
            .Where(l => l.InvoiceId == request.InvoiceId && l.Id == request.LineId)
            .FirstOrDefaultAsync(cancellationToken);

        if (line is null)
        {
            return Error.NotFound("InvoiceLine.NotFound", $"Invoice line with id '{request.LineId}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            line.InvoiceId,
            line.Description,
            line.Quantity,
            line.UnitPrice,
            line.LineTotal
        });

        dbContext.InvoiceLines.Remove(line);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "InvoiceLine.Delete",
            entityType: nameof(InvoiceLine),
            entityId: request.LineId.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }
}
