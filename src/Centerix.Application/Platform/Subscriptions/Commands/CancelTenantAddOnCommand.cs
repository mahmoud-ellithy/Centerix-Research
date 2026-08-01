namespace Centerix.Application.Platform.Subscriptions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.AddOns;

using MediatR;

public record CancelTenantAddOnCommand(Guid Id) : IRequest<Result<Updated>>;

public class CancelTenantAddOnHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CancelTenantAddOnCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(CancelTenantAddOnCommand request, CancellationToken cancellationToken)
    {
        var tenantAddOn = await dbContext.TenantAddOns.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (tenantAddOn is null)
        {
            return Error.NotFound("TenantAddOn.NotFound", $"Tenant add-on with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            tenantAddOn.Status
        });

        var cancelResult = tenantAddOn.Cancel();
        if (!cancelResult.IsSuccess)
        {
            return cancelResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantAddOn.Cancel",
            entityType: nameof(TenantAddOn),
            entityId: tenantAddOn.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                tenantAddOn.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
