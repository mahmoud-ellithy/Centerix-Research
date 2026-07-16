using Centerix.Application.Tenants;
using Finbuckle.MultiTenant.Abstractions;
using Mapster;

namespace Centerix.Infrastructure.Tenancy;

public class TenantService(IMultiTenantStore<CenterixTenantInfo> tenantStore) : ITenantService
{
    private readonly IMultiTenantStore<CenterixTenantInfo> _tenantStore = tenantStore;

    public async Task<List<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _tenantStore.GetAllAsync();
        return tenants.Adapt<List<TenantDto>>();
    }

    public async Task<TenantDto?> GetTenantByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenantStore.TryGetAsync(id);
        return tenant?.Adapt<TenantDto>();
    }

    public async Task<TenantDto> CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default)
    {
        var tenant = new CenterixTenantInfo
        {
            Id = Guid.NewGuid().ToString(),
            Identifier = request.Identifier,
            Name = request.Name,
            ConnectionString = request.ConnectionString,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            ValidUpTo = request.ValidUpTo ?? DateTime.UtcNow.AddMonths(1),
            IsActive = request.IsActive
        };

        await _tenantStore.TryAddAsync(tenant);
        return tenant.Adapt<TenantDto>();
    }

    public async Task DeactivateTenantAsync(string id, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenantStore.TryGetAsync(id);
        if (tenant is null)
        {
            return;
        }

        tenant.IsActive = false;
        await _tenantStore.TryUpdateAsync(tenant);
    }

    public async Task ActivateTenantAsync(string id, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenantStore.TryGetAsync(id);
        if (tenant is null)
        {
            return;
        }

        tenant.IsActive = true;
        await _tenantStore.TryUpdateAsync(tenant);
    }

    public async Task UpdateTenantSubscriptionAsync(string id, DateTime newExpiryDate, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenantStore.TryGetAsync(id);
        if (tenant is null)
        {
            return;
        }

        tenant.ValidUpTo = newExpiryDate;
        await _tenantStore.TryUpdateAsync(tenant);
    }
}