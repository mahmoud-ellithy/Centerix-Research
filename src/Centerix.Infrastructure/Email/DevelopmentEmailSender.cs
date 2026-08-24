namespace Centerix.Infrastructure.Email;

using Centerix.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Development-safe email sender that logs email content to the console.
/// Replace with a real implementation (SMTP, SendGrid, etc.) for production.
/// </summary>
public class DevelopmentEmailSender(ILogger<DevelopmentEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "EMAIL SENT (Development Mode){Separator}To: {ToEmail}{Separator}Subject: {Subject}{Separator}Body: {Body}{Separator}",
            Environment.NewLine, toEmail, Environment.NewLine, subject, Environment.NewLine, htmlBody);

        return Task.CompletedTask;
    }
}
