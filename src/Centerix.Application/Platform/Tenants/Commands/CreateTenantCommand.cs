namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;

public record CreateTenantCommand(
    string Slug,
    string Subdomain,
    string DisplayName,
    string Country,
    string Currency,
    string Timezone,
    string OwnerFirstName,
    string OwnerLastName,
    string OwnerEmail,
    IsolationMode IsolationMode,
    string? LogoUrl = null,
    string? PrimaryColor = null,
    string? OwnerPhone = null) : IRequest<Result<Created>>;

public class CreateTenantHandler(
    IAppDbContext dbContext,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateTenantCommand request,
        CancellationToken cancellationToken)
    {
        var tenantResult = Tenant.Create(
            Guid.NewGuid(),
            request.Slug,
            request.Subdomain,
            request.DisplayName,
            request.Country,
            request.Currency,
            request.Timezone,
            request.OwnerFirstName,
            request.OwnerLastName,
            request.OwnerEmail,
            request.IsolationMode,
            request.LogoUrl,
            request.PrimaryColor,
            request.OwnerPhone);

        if (!tenantResult.IsSuccess)
        {
            return tenantResult.Errors!;
        }

        var tenant = tenantResult.Value;

        dbContext.Tenants.Add(tenant);
        await tenantRegistrySync.SyncCreatedAsync(tenant, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Create",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                tenant.Slug,
                tenant.Subdomain,
                tenant.DisplayName,
                tenant.Country,
                tenant.Currency,
                tenant.OwnerEmail,
                IsolationMode = tenant.IsolationMode.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
