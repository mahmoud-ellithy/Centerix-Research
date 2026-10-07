namespace Centerix.Infrastructure.Email;

/// <summary>
/// SMTP configuration contract (CFG-001). Section: "Smtp".
/// Production startup fails clearly when required values are missing/invalid;
/// development/test environments may leave this unconfigured and use the
/// development/capturing sender instead.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Centerix";
    public bool EnableSsl { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> with a clear message when the
    /// configuration is missing or invalid. Called at startup in Production.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException(
                "Smtp:Host must be configured in Production (e.g. \"Smtp:Host\": \"smtp.example.com\"). " +
                "Production must never silently fall back to the development email sender.");

        if (Port is < 1 or > 65535)
            throw new InvalidOperationException(
                $"Smtp:Port must be between 1 and 65535 (current value: {Port}).");

        if (string.IsNullOrWhiteSpace(FromAddress) || !FromAddress.Contains('@'))
            throw new InvalidOperationException(
                "Smtp:FromAddress must be a valid email address in Production (e.g. \"Smtp:FromAddress\": \"noreply@example.com\").");

        var hasUser = !string.IsNullOrWhiteSpace(Username);
        var hasPass = !string.IsNullOrWhiteSpace(Password);
        if (hasUser != hasPass)
            throw new InvalidOperationException(
                "Smtp:Username and Smtp:Password must either both be configured (authenticated SMTP) " +
                "or both be empty (anonymous relay). Partially configured SMTP credentials are rejected.");
    }
}
