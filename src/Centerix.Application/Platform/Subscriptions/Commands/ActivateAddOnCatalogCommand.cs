namespace Centerix.Application.Platform.Subscriptions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.AddOns;

using MediatR;

public record ActivateAddOnCatalogCommand(int Id) : IRequest<Result<Updated>>;

public class ActivateAddOnCatalogHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<ActivateAddOnCatalogCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(ActivateAddOnCatalogCommand request, CancellationToken cancellationToken)
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

        var activateResult = addOnCatalog.Activate();
        if (!activateResult.IsSuccess)
        {
            return activateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AddOnCatalog.Activate",
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
