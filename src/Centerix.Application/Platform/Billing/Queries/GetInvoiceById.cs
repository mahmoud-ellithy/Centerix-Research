namespace Centerix.Application.Platform.Billing.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetInvoiceByIdQuery(Guid Id) : IRequest<Result<InvoiceDto>>;

public class GetInvoiceByIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetInvoiceByIdQuery, Result<InvoiceDto>>
{
    public async Task<Result<InvoiceDto>> Handle(
        GetInvoiceByIdQuery request,
        CancellationToken cancellationToken)
    {
        var invoice = await dbContext.Invoices
            .Where(i => i.Id == request.Id)
            .ProjectToType<InvoiceDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (invoice is null)
        {
            return Error.NotFound("Invoice.NotFound", $"Invoice with id '{request.Id}' was not found.");
        }

        return invoice;
    }
}
