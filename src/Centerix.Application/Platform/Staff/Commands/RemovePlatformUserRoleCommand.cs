namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record RemovePlatformUserRoleCommand(
    Guid PlatformUserId,
    int RoleId) : IRequest<Result<Deleted>>;

public class RemovePlatformUserRoleHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<RemovePlatformUserRoleCommand, Result<Deleted>>
{
    public async Task<Result<Deleted>> Handle(
        RemovePlatformUserRoleCommand request,
        CancellationToken cancellationToken)
    {
        var userRole = await dbContext.PlatformUserRoles
            .FirstOrDefaultAsync(
                ur => ur.PlatformUserId == request.PlatformUserId && ur.RoleId == request.RoleId,
                cancellationToken);

        if (userRole is null)
        {
            return Error.NotFound("PlatformUserRole.NotFound", $"Role assignment for user '{request.PlatformUserId}' with role '{request.RoleId}' was not found.");
        }

        dbContext.PlatformUserRoles.Remove(userRole);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformUserRole.Remove",
            entityType: nameof(PlatformUserRole),
            entityId: $"{request.PlatformUserId}|{request.RoleId}",
            oldValue: AuditPayload.Serialize(new
            {
                request.PlatformUserId,
                request.RoleId
            }),
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }
}
