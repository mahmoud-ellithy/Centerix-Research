namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;

using MediatR;

public record MarkInvoicePaidCommand(Guid Id) : IRequest<Result<Updated>>;

public class MarkInvoicePaidHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<MarkInvoicePaidCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(MarkInvoicePaidCommand request, CancellationToken cancellationToken)
    {
        var invoice = await dbContext.Invoices.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound("Invoice.NotFound", $"Invoice with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            Status = invoice.Status.ToString()
        });

        var markPaidResult = invoice.MarkPaid();
        if (!markPaidResult.IsSuccess)
        {
            return markPaidResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Invoice.MarkPaid",
            entityType: nameof(Invoice),
            entityId: invoice.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                Status = invoice.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
