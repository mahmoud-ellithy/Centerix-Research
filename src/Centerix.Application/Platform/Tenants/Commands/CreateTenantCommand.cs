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

        dbContext.Tenants.Add(tenantResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Create",
            entityType: nameof(Tenant),
            entityId: tenantResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                tenantResult.Value.Slug,
                tenantResult.Value.Subdomain,
                tenantResult.Value.DisplayName,
                tenantResult.Value.Country,
                tenantResult.Value.Currency,
                tenantResult.Value.OwnerEmail,
                IsolationMode = tenantResult.Value.IsolationMode.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
