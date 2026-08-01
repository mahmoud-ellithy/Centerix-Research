namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;

using MediatR;

public record UpdateTenantCommand(
    Guid Id,
    string DisplayName,
    string? LogoUrl,
    string? PrimaryColor,
    string? OwnerPhone) : IRequest<Result<Updated>>;

public class UpdateTenantHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateTenantCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(UpdateTenantCommand request, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound("Tenant.NotFound", $"Tenant with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            tenant.DisplayName,
            tenant.LogoUrl,
            tenant.PrimaryColor,
            tenant.OwnerPhone
        });

        var updateResult = tenant.Update(
            request.DisplayName,
            request.LogoUrl,
            request.PrimaryColor,
            request.OwnerPhone);

        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Update",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                tenant.DisplayName,
                tenant.LogoUrl,
                tenant.PrimaryColor,
                tenant.OwnerPhone
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
