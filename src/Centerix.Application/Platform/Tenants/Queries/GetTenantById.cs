namespace Centerix.Application.Platform.Tenants.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using MediatR;

using Mapster;

public record GetTenantByIdQuery(Guid Id) : IRequest<Result<TenantDto>>;

public class GetTenantByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantByIdQuery, Result<TenantDto>>
{
    public async Task<Result<TenantDto>> Handle(GetTenantByIdQuery request, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .Where(t => t.Id == request.Id)
            .ProjectToType<TenantDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
        {
            return Error.NotFound("Tenant.NotFound", $"Tenant with id '{request.Id}' was not found.");
        }

        return tenant;
    }
}
