namespace Centerix.Application.Platform.Subscriptions.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetTenantAddOnsQuery : IRequest<Result<IEnumerable<TenantAddOnDto>>>;

public class GetTenantAddOnsHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantAddOnsQuery, Result<IEnumerable<TenantAddOnDto>>>
{
    public async Task<Result<IEnumerable<TenantAddOnDto>>> Handle(
        GetTenantAddOnsQuery request,
        CancellationToken cancellationToken)
    {
        var tenantAddOns = await dbContext.TenantAddOns
            .ProjectToType<TenantAddOnDto>()
            .ToListAsync(cancellationToken);

        return tenantAddOns;
    }
}
