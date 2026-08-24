namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Abstraction for sending emails. Implementations may use SMTP, SendGrid, or console logging for development.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an email asynchronously.
    /// </summary>
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default);
}
