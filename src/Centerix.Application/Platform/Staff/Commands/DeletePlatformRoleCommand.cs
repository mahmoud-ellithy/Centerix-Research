namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;

public record DeletePlatformRoleCommand(int Id) : IRequest<Result<Deleted>>;

public class DeletePlatformRoleHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<DeletePlatformRoleCommand, Result<Deleted>>
{
    public async Task<Result<Deleted>> Handle(
        DeletePlatformRoleCommand request,
        CancellationToken cancellationToken)
    {
        var role = await dbContext.PlatformRoles.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (role is null)
        {
            return Error.NotFound("PlatformRole.NotFound", $"Platform role with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            role.Code,
            role.DisplayName
        });

        dbContext.PlatformRoles.Remove(role);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformRole.Delete",
            entityType: nameof(PlatformRole),
            entityId: request.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }
}
