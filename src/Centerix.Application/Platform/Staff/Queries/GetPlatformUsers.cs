namespace Centerix.Application.Platform.Staff.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetPlatformUsersQuery : IRequest<Result<IEnumerable<PlatformUserDto>>>;

public class GetPlatformUsersHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPlatformUsersQuery, Result<IEnumerable<PlatformUserDto>>>
{
    public async Task<Result<IEnumerable<PlatformUserDto>>> Handle(
        GetPlatformUsersQuery request,
        CancellationToken cancellationToken)
    {
        var users = await dbContext.PlatformUsers
            .OrderBy(u => u.FullName)
            .ProjectToType<PlatformUserDto>()
            .ToListAsync(cancellationToken);

        return users;
    }
}
