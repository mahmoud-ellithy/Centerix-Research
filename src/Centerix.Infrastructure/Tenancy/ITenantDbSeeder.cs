namespace Centerix.Infrastructure.Tenancy;

public interface ITenantDbSeeder
{
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
}