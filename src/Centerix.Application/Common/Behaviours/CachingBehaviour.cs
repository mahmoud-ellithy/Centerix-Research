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
        // Fail-closed: skip caching when tenant is not authorized (verified) to prevent
        // cross-tenant cache leakage. Using IsAuthorized (not IsResolved) ensures that only
        // requests that passed TenantGuardMiddleware membership verification participate in
        // the cache, and the TenantId used in the key is the verified tenant, not the raw
        // client-resolved value.
        if (!currentTenant.IsAuthorized)
        {
            logger.LogWarning("Cache skipped for {RequestName}: tenant not authorized", typeof(TRequest).Name);
            return await next();
        }

        var requestName = typeof(TRequest).Name;
        var cacheKey = $"{currentTenant.TenantId}:{requestName}:{request.GetCacheKey()}";

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