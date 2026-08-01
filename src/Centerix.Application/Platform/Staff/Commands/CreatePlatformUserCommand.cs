namespace Centerix.Application.Platform.Staff.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Staff;

using MediatR;

public record CreatePlatformUserCommand(
    string Email,
    string FullName,
    string Password) : IRequest<Result<Created>>;

public class CreatePlatformUserHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreatePlatformUserCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreatePlatformUserCommand request,
        CancellationToken cancellationToken)
    {
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        var userResult = PlatformUser.Create(
            Guid.NewGuid(),
            request.Email,
            request.FullName,
            passwordHash);

        if (!userResult.IsSuccess)
        {
            return userResult.Errors!;
        }

        dbContext.PlatformUsers.Add(userResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "PlatformUser.Create",
            entityType: nameof(PlatformUser),
            entityId: userResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                userResult.Value.Email,
                userResult.Value.FullName,
                userResult.Value.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
