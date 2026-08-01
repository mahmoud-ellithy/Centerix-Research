namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;

public record CreatePlatformRoleCommand(
    string Code,
    string DisplayName) : IRequest<Result<Created>>;

public class CreatePlatformRoleHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreatePlatformRoleCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreatePlatformRoleCommand request,
        CancellationToken cancellationToken)
    {
        var roleResult = PlatformRole.Create(
            0,
            request.Code,
            request.DisplayName);

        if (!roleResult.IsSuccess)
        {
            return roleResult.Errors!;
        }

        dbContext.PlatformRoles.Add(roleResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformRole.Create",
            entityType: nameof(PlatformRole),
            entityId: roleResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                roleResult.Value.Code,
                roleResult.Value.DisplayName
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
