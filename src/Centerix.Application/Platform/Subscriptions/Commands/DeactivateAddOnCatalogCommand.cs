namespace Centerix.Application.Platform.Subscriptions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.AddOns;

using MediatR;

public record DeactivateAddOnCatalogCommand(int Id) : IRequest<Result<Updated>>;

public class DeactivateAddOnCatalogHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<DeactivateAddOnCatalogCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(DeactivateAddOnCatalogCommand request, CancellationToken cancellationToken)
    {
        var addOnCatalog = await dbContext.AddOnCatalogs.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (addOnCatalog is null)
        {
            return Error.NotFound("AddOnCatalog.NotFound", $"Add-on catalog with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            addOnCatalog.IsActive
        });

        var deactivateResult = addOnCatalog.Deactivate();
        if (!deactivateResult.IsSuccess)
        {
            return deactivateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AddOnCatalog.Deactivate",
            entityType: nameof(AddOnCatalog),
            entityId: addOnCatalog.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                addOnCatalog.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
