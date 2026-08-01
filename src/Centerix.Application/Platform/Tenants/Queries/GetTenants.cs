namespace Centerix.Application.Platform.Tenants.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetTenantsQuery : IRequest<Result<IEnumerable<TenantDto>>>;

public class GetTenantsHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantsQuery, Result<IEnumerable<TenantDto>>>
{
    public async Task<Result<IEnumerable<TenantDto>>> Handle(
        GetTenantsQuery request,
        CancellationToken cancellationToken)
    {
        var tenants = await dbContext.Tenants
            .ProjectToType<TenantDto>()
            .ToListAsync(cancellationToken);

        return tenants;
    }
}
