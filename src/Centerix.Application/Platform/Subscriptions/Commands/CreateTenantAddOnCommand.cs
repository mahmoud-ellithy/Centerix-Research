namespace Centerix.Application.Platform.Subscriptions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.AddOns;
using Centerix.Domain.Platform.Subscriptions.AddOns.Enums;

using MediatR;

public record CreateTenantAddOnCommand(
    int AddOnCatalogId,
    int Quantity,
    decimal SnapshotUnitPrice,
    DateTime EffectiveFrom) : IRequest<Result<Created>>;

public class CreateTenantAddOnHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantAddOnCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(CreateTenantAddOnCommand request, CancellationToken cancellationToken)
    {
        var result = TenantAddOn.Create(
            Guid.NewGuid(),
            request.AddOnCatalogId,
            request.Quantity,
            request.SnapshotUnitPrice,
            request.EffectiveFrom,
            null,
            TenantAddOnStatus.Active);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.TenantAddOns.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantAddOn.Create",
            entityType: nameof(TenantAddOn),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.AddOnCatalogId,
                result.Value.Quantity,
                result.Value.SnapshotUnitPrice,
                result.Value.EffectiveFrom,
                result.Value.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
