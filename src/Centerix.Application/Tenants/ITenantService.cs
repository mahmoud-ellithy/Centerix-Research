namespace Centerix.Application.Tenants;

public interface ITenantService
{
    Task<List<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default);
    Task<TenantDto?> GetTenantByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<TenantDto> CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default);
    Task DeactivateTenantAsync(string id, CancellationToken cancellationToken = default);
    Task ActivateTenantAsync(string id, CancellationToken cancellationToken = default);
    Task UpdateTenantSubscriptionAsync(string id, DateTime newExpiryDate, CancellationToken cancellationToken = default);
}