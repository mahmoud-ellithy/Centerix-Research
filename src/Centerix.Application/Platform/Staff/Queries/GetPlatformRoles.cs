namespace Centerix.Application.Platform.Staff.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetPlatformRolesQuery : IRequest<Result<IEnumerable<PlatformRoleDto>>>;

public class GetPlatformRolesHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPlatformRolesQuery, Result<IEnumerable<PlatformRoleDto>>>
{
    public async Task<Result<IEnumerable<PlatformRoleDto>>> Handle(
        GetPlatformRolesQuery request,
        CancellationToken cancellationToken)
    {
        var roles = await dbContext.PlatformRoles
            .OrderBy(r => r.DisplayName)
            .ProjectToType<PlatformRoleDto>()
            .ToListAsync(cancellationToken);

        return roles;
    }
}
