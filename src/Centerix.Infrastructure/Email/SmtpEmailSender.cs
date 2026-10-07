namespace Centerix.Infrastructure.Email;

using Centerix.Application.Common.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

/// <summary>
/// Production SMTP email sender (CFG-001), implemented with MailKit.
/// System.Net.Mail.SmtpClient is deliberately NOT used (obsolete).
/// Configuration is validated eagerly: missing/invalid SMTP settings fail fast
/// with a clear error instead of silently degrading to a development sender.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
        // Eager fail-fast: constructing this sender with bad config is a startup bug.
        _options.Validate();
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
            throw new ArgumentException("Recipient email is required.", nameof(toEmail));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject ?? string.Empty;
        message.Body = new TextPart(MimeKit.Text.TextFormat.Html) { Text = htmlBody ?? string.Empty };

        using var client = new SmtpClient();
        try
        {
            var socketOptions = _options.EnableSsl
                ? _options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
                : SecureSocketOptions.None;

            await client.ConnectAsync(_options.Host, _options.Port, socketOptions, cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.Username))
                await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP email delivery failed to {ToEmail} via {Host}:{Port}.", toEmail, _options.Host, _options.Port);
            throw;
        }
    }
}
