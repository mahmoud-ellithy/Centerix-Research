namespace Centerix.Application.Platform.Billing.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetInvoicesQuery : IRequest<Result<IEnumerable<InvoiceDto>>>;

public class GetInvoicesHandler(IAppDbContext dbContext)
    : IRequestHandler<GetInvoicesQuery, Result<IEnumerable<InvoiceDto>>>
{
    public async Task<Result<IEnumerable<InvoiceDto>>> Handle(
        GetInvoicesQuery request,
        CancellationToken cancellationToken)
    {
        var invoices = await dbContext.Invoices
            .OrderByDescending(i => i.CreatedAtUtc)
            .ProjectToType<InvoiceDto>()
            .ToListAsync(cancellationToken);

        return invoices;
    }
}
