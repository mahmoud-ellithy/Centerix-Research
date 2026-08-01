namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;

public record AssignPlatformRolePermissionCommand(
    int RoleId,
    int PermissionId) : IRequest<Result<Created>>;

public class AssignPlatformRolePermissionHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<AssignPlatformRolePermissionCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        AssignPlatformRolePermissionCommand request,
        CancellationToken cancellationToken)
    {
        var rolePermissionResult = PlatformRolePermission.Create(
            request.RoleId,
            request.PermissionId);

        if (!rolePermissionResult.IsSuccess)
        {
            return rolePermissionResult.Errors!;
        }

        dbContext.PlatformRolePermissions.Add(rolePermissionResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformRolePermission.Assign",
            entityType: nameof(PlatformRolePermission),
            entityId: $"{request.RoleId}|{request.PermissionId}",
            newValue: AuditPayload.Serialize(new
            {
                request.RoleId,
                request.PermissionId
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
