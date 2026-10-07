using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FwdHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;
using SystemIPNetwork = System.Net.IPNetwork;

namespace Centerix.API.Infrastructure;

/// <summary>
/// Configuration-bound forwarded-header trust (F1: X-Forwarded-For spoofing of the rate-limit
/// partition key).
/// <para>
/// The runtime's built-in trust lists are deliberately REPLACED, not extended: only the proxies
/// and networks declared in the <c>ForwardedHeaders</c> configuration section are ever trusted,
/// and X-Forwarded-For processing is switched on only when at least one such entry exists. With
/// no configuration the pipeline is strict direct-peer-only (<see cref="FwdHeaders.None"/>).
/// </para>
/// <para>
/// Section shape (all values optional; defaults shown):
/// <code>
/// "ForwardedHeaders": {
///   "KnownProxies": [ "127.0.0.1", "::1" ],   // exact proxy IPs (peer addresses we trust)
///   "KnownNetworks": [ "127.0.0.0/8" ],        // proxy networks in CIDR notation
///   "ForwardLimit": 5                          // how many X-Forwarded-For hops may be consumed
/// }
/// </code>
/// Typical deployments: direct Kestrel → no entries needed; local nginx / IIS-ANCM in front →
/// loopback entries above; a cross-machine load balancer → add its IP or CIDR range here.
/// </para>
/// <para>
/// Invariants enforced (fail-fast at startup via <c>ValidateOnStart</c>):
/// <list type="bullet">
///   <item>flags != None ⇒ at least one trusted proxy/network (an empty trust list with
///   forwarding enabled would make the runtime trust EVERY peer);</item>
///   <item>ForwardLimit ≥ 1 (the framework default of 1 silently breaks multi-hop chains).</item>
/// </list>
/// Malformed IP/CIDR entries throw during options resolution, i.e. at host startup.
/// </para>
/// </summary>
public static class ForwardedHeadersProtection
{
    public const string SectionName = "ForwardedHeaders";

    public static IServiceCollection AddForwardedHeadersProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure(options => ConfigureFromSection(options, section))
            .Validate(
                options => options.ForwardLimit is null or >= 1,
                "ForwardedHeaders:ForwardLimit must be at least 1.")
            .Validate(
                options => options.ForwardedHeaders == FwdHeaders.None
                    || options.KnownProxies.Count + options.KnownIPNetworks.Count > 0,
                "ForwardedHeaders: X-ForwardedFor processing requires at least one trusted proxy or network entry.")
            .ValidateOnStart();

        return services;
    }

    private static void ConfigureFromSection(ForwardedHeadersOptions options, IConfigurationSection section)
    {
        // Start from an EMPTY trust set. The framework defaults (::1 + 127.0.0.0/8) are silently
        // trusted even without configuration; carrying them into an operator-configured list would
        // be a trust decision nobody made. Only configuration entries may become trusted.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var value in ListValues(section.GetSection("KnownProxies")))
        {
            if (!IPAddress.TryParse(value, out var proxy))
            {
                throw Invalid($"{SectionName}:KnownProxies contains '{value}', which is not a valid IP address.");
            }

            options.KnownProxies.Add(proxy);
        }

        foreach (var value in ListValues(section.GetSection("KnownNetworks")))
        {
            if (!SystemIPNetwork.TryParse(value, out var network))
            {
                throw Invalid($"{SectionName}:KnownNetworks contains '{value}', which is not a valid CIDR network (e.g. 10.0.0.0/8).");
            }

            options.KnownIPNetworks.Add(network);
        }

        // INVARIANT: forwarding flags and the trust lists must agree in BOTH directions.
        // Entries present ⇒ X-ForwardedFor processing on; no entries ⇒ off (direct-peer-only).
        var trustEntries = options.KnownProxies.Count + options.KnownIPNetworks.Count;
        options.ForwardedHeaders = trustEntries > 0 ? FwdHeaders.XForwardedFor : FwdHeaders.None;

        var forwardLimitRaw = section["ForwardLimit"];
        if (forwardLimitRaw is null)
        {
            // Default 5: enough for a client → edge → origin chain; the framework default of 1
            // would stop after one hop and resolve the address to the last trusted proxy instead
            // of the originating client, collapsing every caller behind that proxy into one
            // rate-limit partition.
            options.ForwardLimit = 5;
        }
        else if (int.TryParse(forwardLimitRaw, out var forwardLimit))
        {
            options.ForwardLimit = forwardLimit; // < 1 rejected by ValidateOnStart
        }
        else
        {
            throw Invalid($"{SectionName}:ForwardLimit '{forwardLimitRaw}' is not an integer.");
        }
    }

    private static IEnumerable<string> ListValues(IConfigurationSection section)
        => section.GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());

    private static OptionsValidationException Invalid(string message)
        => new(nameof(ForwardedHeadersOptions), typeof(ForwardedHeadersOptions), [message]);
}
