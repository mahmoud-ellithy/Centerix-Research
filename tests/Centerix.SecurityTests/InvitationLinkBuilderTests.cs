using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Auth;
using Microsoft.Extensions.Options;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Unit tests for the configuration-driven invitation link builder (H1).
/// The base URL comes exclusively from configuration — no environment may be assumed.
/// </summary>
public class InvitationLinkBuilderTests
{
    private static IInvitationLinkBuilder CreateBuilder(string baseUrl)
        => new InvitationLinkBuilder(Options.Create(new InvitationLinkOptions { BaseUrl = baseUrl }));

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com/invitations/abc123/accept")]
    [InlineData("https://app.example.com/", "https://app.example.com/invitations/abc123/accept")]
    [InlineData("https://portal.staging.example.co.uk/app/", "https://portal.staging.example.co.uk/app/invitations/abc123/accept")]
    [InlineData("http://localhost:5001", "http://localhost:5001/invitations/abc123/accept")]
    public void BuildAcceptLink_UsesConfiguredBaseUrl(string baseUrl, string expected)
    {
        var link = CreateBuilder(baseUrl).BuildAcceptLink("abc123");

        Assert.Equal(expected, link.AbsoluteUri);
    }

    [Fact]
    public void BuildAcceptLink_EscapesPathUnsafeCharacters()
    {
        // '/' must be escaped so a crafted token cannot add path segments.
        // (Real tokens are base64url — no '/', '+', '=', '?', '#' — this proves defense in depth.)
        var link = CreateBuilder("https://app.example.com").BuildAcceptLink("a/b");

        Assert.Equal("https://app.example.com/invitations/a%2Fb/accept", link.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://files.example.com")]
    [InlineData("/relative/path")]
    public void BuildAcceptLink_MissingOrInvalidBaseUrl_ThrowsClearError(string baseUrl)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateBuilder(baseUrl).BuildAcceptLink("abc123"));

        Assert.Contains("Invitations:BaseUrl", ex.Message);
    }
}
