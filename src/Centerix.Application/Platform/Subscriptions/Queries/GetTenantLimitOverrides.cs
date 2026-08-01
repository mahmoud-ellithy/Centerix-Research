namespace Centerix.Application.Platform.Subscriptions.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetTenantLimitOverridesQuery : IRequest<Result<IEnumerable<TenantLimitOverrideDto>>>;

public class GetTenantLimitOverridesHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantLimitOverridesQuery, Result<IEnumerable<TenantLimitOverrideDto>>>
{
    public async Task<Result<IEnumerable<TenantLimitOverrideDto>>> Handle(
        GetTenantLimitOverridesQuery request,
        CancellationToken cancellationToken)
    {
        var tenantLimitOverrides = await dbContext.TenantLimitOverrides
            .ProjectToType<TenantLimitOverrideDto>()
            .ToListAsync(cancellationToken);

        return tenantLimitOverrides;
    }
}
