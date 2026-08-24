using System.Security.Cryptography;
using System.Text;
using Centerix.Application.Common.Interfaces;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Test double for <see cref="IEmailSender"/> that records every sent e-mail so tests can recover
/// the raw invitation token embedded in the e-mail link (the raw token is never persisted anywhere).
/// </summary>
public class CapturingEmailSender : IEmailSender
{
    public sealed record SentEmail(string To, string Subject, string Body);

    private readonly List<SentEmail> _sent = [];

    public IReadOnlyList<SentEmail> Sent => _sent;

    public void Clear()
    {
        lock (_sent)
        {
            _sent.Clear();
        }
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        lock (_sent)
        {
            _sent.Add(new SentEmail(to, subject, body));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Shared helpers for invitation-token-based tests.
/// </summary>
public static class TestInviteTokens
{
    /// <summary>
    /// Generates a cryptographically random invitation token using the SAME shape as production
    /// (32 random bytes, base64url, no padding).
    /// </summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Mirrors the server-side hashing (SHA-256, lowercase hex) so tests can seed invitation rows
    /// in arbitrary states without going through the create endpoint.
    /// </summary>
    public static string Sha256Hex(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the raw invitation token from an e-mail body produced by CreateInvitationHandler.
    /// </summary>
    public static string ExtractTokenFromEmailBody(string body)
    {
        var marker = "invitations/";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No invitation link found in e-mail body: {body}");

        start += marker.Length;
        var end = body.IndexOf('/', start);
        Assert.True(end > start, $"Malformed invitation link in e-mail body: {body}");

        var encoded = body[start..end];
        return Uri.UnescapeDataString(encoded);
    }
}
