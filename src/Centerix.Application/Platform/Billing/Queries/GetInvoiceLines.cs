namespace Centerix.Application.Platform.Billing.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetInvoiceLinesQuery(Guid InvoiceId) : IRequest<Result<IEnumerable<InvoiceLineDto>>>;

public class GetInvoiceLinesHandler(IAppDbContext dbContext)
    : IRequestHandler<GetInvoiceLinesQuery, Result<IEnumerable<InvoiceLineDto>>>
{
    public async Task<Result<IEnumerable<InvoiceLineDto>>> Handle(
        GetInvoiceLinesQuery request,
        CancellationToken cancellationToken)
    {
        var lines = await dbContext.InvoiceLines
            .Where(l => l.InvoiceId == request.InvoiceId)
            .ProjectToType<InvoiceLineDto>()
            .ToListAsync(cancellationToken);

        return lines;
    }
}
