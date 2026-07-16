using Centerix.Application.Common.Interfaces;
using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Centerix.Application.Common.Behaviours;

public class CachingBehaviour<TRequest, TResponse>(
    HybridCache cache,
    ICurrentTenant currentTenant,
    ILogger<TRequest> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICachedQuery
    where TResponse : class
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var tenantKey = currentTenant.IsResolved ? currentTenant.TenantId : "global";
        var cacheKey = $"{tenantKey}:{requestName}:{request.GetCacheKey()}";

        logger.LogInformation("Checking cache for {RequestName} with key {CacheKey}",
            requestName, cacheKey);

        var cachedResponse = await cache.GetOrCreateAsync(
            cacheKey,
            async token => await next(),
            cancellationToken: cancellationToken);

        var cacheStatus = cachedResponse != null ? "hit" : "miss";

        logger.LogInformation("Cache {CacheStatus} for {RequestName}", cacheStatus, requestName);

        return cachedResponse!;
    }
}

public interface ICachedQuery
{
    string GetCacheKey();
}