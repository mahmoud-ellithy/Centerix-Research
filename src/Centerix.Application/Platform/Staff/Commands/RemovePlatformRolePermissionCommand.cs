namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record RemovePlatformRolePermissionCommand(
    int RoleId,
    int PermissionId) : IRequest<Result<Deleted>>;

public class RemovePlatformRolePermissionHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<RemovePlatformRolePermissionCommand, Result<Deleted>>
{
    public async Task<Result<Deleted>> Handle(
        RemovePlatformRolePermissionCommand request,
        CancellationToken cancellationToken)
    {
        var rolePermission = await dbContext.PlatformRolePermissions
            .FirstOrDefaultAsync(
                rp => rp.RoleId == request.RoleId && rp.PermissionId == request.PermissionId,
                cancellationToken);

        if (rolePermission is null)
        {
            return Error.NotFound("PlatformRolePermission.NotFound", $"Permission assignment for role '{request.RoleId}' with permission '{request.PermissionId}' was not found.");
        }

        dbContext.PlatformRolePermissions.Remove(rolePermission);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformRolePermission.Remove",
            entityType: nameof(PlatformRolePermission),
            entityId: $"{request.RoleId}|{request.PermissionId}",
            oldValue: AuditPayload.Serialize(new
            {
                request.RoleId,
                request.PermissionId
            }),
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }
}
