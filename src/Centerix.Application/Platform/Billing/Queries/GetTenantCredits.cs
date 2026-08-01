namespace Centerix.Application.Platform.Billing.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetTenantCreditsQuery : IRequest<Result<IEnumerable<TenantCreditDto>>>;

public class GetTenantCreditsHandler(IAppDbContext dbContext)
    : IRequestHandler<GetTenantCreditsQuery, Result<IEnumerable<TenantCreditDto>>>
{
    public async Task<Result<IEnumerable<TenantCreditDto>>> Handle(
        GetTenantCreditsQuery request,
        CancellationToken cancellationToken)
    {
        var credits = await dbContext.TenantCredits
            .OrderByDescending(c => c.CreatedAtUtc)
            .ProjectToType<TenantCreditDto>()
            .ToListAsync(cancellationToken);

        return credits;
    }
}
