namespace Centerix.Application.Platform.Subscriptions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.LimitOverrides;

using MediatR;

public record CreateTenantLimitOverrideCommand(
    string LimitType,
    int OverrideValue,
    string? Reason) : IRequest<Result<Created>>;

public class CreateTenantLimitOverrideHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantLimitOverrideCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(CreateTenantLimitOverrideCommand request, CancellationToken cancellationToken)
    {
        var result = TenantLimitOverride.Create(
            Guid.NewGuid(),
            request.LimitType,
            request.OverrideValue,
            request.Reason);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.TenantLimitOverrides.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantLimitOverride.Create",
            entityType: nameof(TenantLimitOverride),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.LimitType,
                result.Value.OverrideValue,
                result.Value.Reason
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
