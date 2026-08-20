using Centerix.Domain.Platform.Tenants;

namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Synchronizes the canonical Platform.Tenants state to the derived
/// Finbuckle TenantRegistry projection. All mutations must be atomic
/// with the domain write.
/// </summary>
public interface ITenantRegistrySync
{
    Task SyncCreatedAsync(Tenant tenant, CancellationToken cancellationToken = default);
    Task SyncLifecycleAsync(Tenant tenant, CancellationToken cancellationToken = default);
    Task SyncMetadataAsync(Tenant tenant, CancellationToken cancellationToken = default);
}
