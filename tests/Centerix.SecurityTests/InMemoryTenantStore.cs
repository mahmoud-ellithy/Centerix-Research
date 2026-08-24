using System.Collections.Concurrent;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;

namespace Centerix.SecurityTests;

public class InMemoryTenantStore : IMultiTenantStore<CenterixTenantInfo>
{
    private readonly ConcurrentDictionary<string, CenterixTenantInfo> _store = new();

    public Task<CenterixTenantInfo?> TryGetAsync(string id)
        => Task.FromResult(_store.TryGetValue(id, out var info) ? info : null);

    public Task<CenterixTenantInfo?> TryGetByIdentifierAsync(string identifier)
        => Task.FromResult(_store.Values.FirstOrDefault(t => t.Identifier == identifier));

    public Task<IEnumerable<CenterixTenantInfo>> GetAllAsync()
        => Task.FromResult<IEnumerable<CenterixTenantInfo>>(_store.Values.ToList());

    public Task<bool> TryAddAsync(CenterixTenantInfo info)
        => Task.FromResult(_store.TryAdd(info.Id, info));

    public Task<bool> TryUpdateAsync(CenterixTenantInfo info)
    {
        _store[info.Id] = info;
        return Task.FromResult(true);
    }

    public Task<bool> TryRemoveAsync(string id)
        => Task.FromResult(_store.TryRemove(id, out _));
}
