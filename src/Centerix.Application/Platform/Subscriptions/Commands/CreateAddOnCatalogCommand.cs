namespace Centerix.Application.Platform.Subscriptions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.AddOns;
using Centerix.Domain.Platform.Subscriptions.AddOns.Enums;

using MediatR;

public record CreateAddOnCatalogCommand(
    string Code,
    string DisplayName,
    string UnitType,
    int UnitQuantity,
    byte BillingType) : IRequest<Result<Created>>;

public class CreateAddOnCatalogHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateAddOnCatalogCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(CreateAddOnCatalogCommand request, CancellationToken cancellationToken)
    {
        var billingType = (AddOnBillingType)request.BillingType;

        var result = AddOnCatalog.Create(
            0,
            request.Code,
            request.DisplayName,
            request.UnitType,
            request.UnitQuantity,
            billingType);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.AddOnCatalogs.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AddOnCatalog.Create",
            entityType: nameof(AddOnCatalog),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.Code,
                result.Value.DisplayName,
                result.Value.UnitType,
                result.Value.UnitQuantity,
                result.Value.BillingType,
                result.Value.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
