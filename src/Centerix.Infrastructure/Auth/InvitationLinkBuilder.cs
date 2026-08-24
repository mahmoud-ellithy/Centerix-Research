using Centerix.Application.Common.Interfaces;
using Microsoft.Extensions.Options;

namespace Centerix.Infrastructure.Auth;

/// <summary>
/// Configuration for outbound invitation links.
/// <see cref="BaseUrl"/> must be an absolute URI pointing at the front end that serves the
/// invitation acceptance screen. It is intentionally NOT given a default: each environment
/// (development, staging, production) must configure its own value and startup fails fast
/// when it is missing or not an absolute URL.
/// </summary>
public class InvitationLinkOptions
{
    public const string SectionName = "Invitations";

    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Default <see cref="IInvitationLinkBuilder"/>. Combines the configured application base URL with
/// the invitation accept route. The raw token is percent-encoded so base64url characters survive
/// URL rewriting unchanged.
/// </summary>
public class InvitationLinkBuilder(IOptions<InvitationLinkOptions> options) : IInvitationLinkBuilder
{
    private readonly InvitationLinkOptions _options = options.Value;

    public Uri BuildAcceptLink(string token)
    {
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"Invitations:{nameof(InvitationLinkOptions.BaseUrl)} is missing or not an absolute http(s) URL. " +
                "Configure it per environment (e.g. \"Invitations:BaseUrl\": \"https://app.example.com\").");
        }

        // Ensures the combined path is appended to the base path, not replacing it.
        var baseWithSlash = new Uri(baseUrl, baseUrl.AbsolutePath.EndsWith('/') ? "." : "./");
        return new Uri(baseWithSlash, $"invitations/{Uri.EscapeDataString(token)}/accept");
    }
}
