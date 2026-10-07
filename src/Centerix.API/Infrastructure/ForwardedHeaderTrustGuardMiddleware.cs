using Microsoft.AspNetCore.Http;

namespace Centerix.API.Infrastructure;

/// <summary>
/// Fail-closed guard for the forwarded-header trust chain (F1).
/// <para>
/// <c>ForwardedHeadersMiddleware</c> validates the X-Forwarded-For chain against its configured
/// trust lists only while it has a socket peer to start the walk from. A request whose peer
/// address is null cannot prove which proxy chain it arrived through — yet the shipped runtime
/// adopts the client-controlled X-Forwarded-For value from a null peer even when the trust lists
/// are non-empty. Because the rate limiter partitions on the resolved address, that would let a
/// client pick its own partition and poison another one.
/// </para>
/// <para>
/// This middleware runs BEFORE <c>ForwardedHeadersMiddleware</c> and strips the header whenever
/// there is no peer to attribute it to, so forwarding processing can only ever begin from a
/// proven socket address. Peer-less requests simply keep the shared "unknown" partition.
/// </para>
/// </summary>
public class ForwardedHeaderTrustGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is null)
        {
            context.Request.Headers.Remove("X-Forwarded-For");
        }

        await next(context);
    }
}
