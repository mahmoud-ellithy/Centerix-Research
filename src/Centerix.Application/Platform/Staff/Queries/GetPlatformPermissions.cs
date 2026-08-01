namespace Centerix.Application.Platform.Staff.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetPlatformPermissionsQuery : IRequest<Result<IEnumerable<PlatformPermissionDto>>>;

public class GetPlatformPermissionsHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPlatformPermissionsQuery, Result<IEnumerable<PlatformPermissionDto>>>
{
    public async Task<Result<IEnumerable<PlatformPermissionDto>>> Handle(
        GetPlatformPermissionsQuery request,
        CancellationToken cancellationToken)
    {
        var permissions = await dbContext.PlatformPermissions
            .OrderBy(p => p.Module)
            .ThenBy(p => p.Action)
            .ProjectToType<PlatformPermissionDto>()
            .ToListAsync(cancellationToken);

        return permissions;
    }
}
