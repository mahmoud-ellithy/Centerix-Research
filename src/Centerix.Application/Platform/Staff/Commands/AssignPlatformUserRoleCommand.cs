namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;

public record AssignPlatformUserRoleCommand(
    Guid PlatformUserId,
    int RoleId) : IRequest<Result<Created>>;

public class AssignPlatformUserRoleHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<AssignPlatformUserRoleCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        AssignPlatformUserRoleCommand request,
        CancellationToken cancellationToken)
    {
        var userRoleResult = PlatformUserRole.Create(
            request.PlatformUserId,
            request.RoleId);

        if (!userRoleResult.IsSuccess)
        {
            return userRoleResult.Errors!;
        }

        dbContext.PlatformUserRoles.Add(userRoleResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformUserRole.Assign",
            entityType: nameof(PlatformUserRole),
            entityId: $"{request.PlatformUserId}|{request.RoleId}",
            newValue: AuditPayload.Serialize(new
            {
                request.PlatformUserId,
                request.RoleId
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
