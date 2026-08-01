namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;

using MediatR;

public record IssueInvoiceCommand(Guid Id, DateTime IssuedAt, DateTime? DueAt) : IRequest<Result<Updated>>;

public class IssueInvoiceHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<IssueInvoiceCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(IssueInvoiceCommand request, CancellationToken cancellationToken)
    {
        var invoice = await dbContext.Invoices.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound("Invoice.NotFound", $"Invoice with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            Status = invoice.Status.ToString(),
            invoice.IssuedAt,
            invoice.DueAt
        });

        var issueResult = invoice.Issue(request.IssuedAt, request.DueAt);
        if (!issueResult.IsSuccess)
        {
            return issueResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Invoice.Issue",
            entityType: nameof(Invoice),
            entityId: invoice.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                Status = invoice.Status.ToString(),
                invoice.IssuedAt,
                invoice.DueAt
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
