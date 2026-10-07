using System.Net;
using Microsoft.AspNetCore.Http;

namespace Centerix.API.Infrastructure;

/// <summary>
/// Canonical client-address resolution for per-source controls (F1: forwarded-header trust).
/// </summary>
public static class ClientIp
{
    /// <summary>
    /// Normalizes a socket peer to its canonical string form so a single client can never occupy
    /// two rate-limit partitions (an IPv4 address expressed as an IPv4-mapped IPv6 address and the
    /// same address expressed directly must resolve to the same partition key).
    /// Returns null when there is no socket peer.
    /// </summary>
    public static string? Normalize(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    /// <summary>
    /// The rate-limit partition key for the current request. A request with no socket peer
    /// falls back to the shared "unknown" bucket — never to a client-controlled header value.
    /// </summary>
    public static string ForRateLimit(HttpContext context)
        => Normalize(context.Connection.RemoteIpAddress) ?? "unknown";
}
