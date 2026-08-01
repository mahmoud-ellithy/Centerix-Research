namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;

public record UpdatePlatformUserCommand(
    Guid Id,
    string? FullName,
    bool IsActive) : IRequest<Result<Updated>>;

public class UpdatePlatformUserHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdatePlatformUserCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdatePlatformUserCommand request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.PlatformUsers.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (user is null)
        {
            return Error.NotFound("PlatformUser.NotFound", $"Platform user with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            user.FullName,
            user.IsActive
        });

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            var updateResult = user.UpdateFullName(request.FullName);
            if (!updateResult.IsSuccess)
            {
                return updateResult.Errors!;
            }
        }

        if (request.IsActive && !user.IsActive)
        {
            var reactivateResult = user.Reactivate();
            if (!reactivateResult.IsSuccess)
            {
                return reactivateResult.Errors!;
            }
        }
        else if (!request.IsActive && user.IsActive)
        {
            var deactivateResult = user.Deactivate();
            if (!deactivateResult.IsSuccess)
            {
                return deactivateResult.Errors!;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformUser.Update",
            entityType: nameof(PlatformUser),
            entityId: user.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                user.FullName,
                user.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
