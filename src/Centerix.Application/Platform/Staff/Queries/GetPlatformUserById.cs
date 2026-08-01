namespace Centerix.Application.Platform.Staff.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetPlatformUserByIdQuery(Guid Id) : IRequest<Result<PlatformUserDto>>;

public class GetPlatformUserByIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPlatformUserByIdQuery, Result<PlatformUserDto>>
{
    public async Task<Result<PlatformUserDto>> Handle(
        GetPlatformUserByIdQuery request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.PlatformUsers
            .Where(u => u.Id == request.Id)
            .ProjectToType<PlatformUserDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return Error.NotFound("PlatformUser.NotFound", $"Platform user with id '{request.Id}' was not found.");
        }

        return user;
    }
}
