namespace Centerix.Infrastructure.Email;

using Microsoft.Extensions.Hosting;

/// <summary>
/// Email delivery mode (REV-012). Section: "Email".
/// <list type="bullet">
/// <item><see cref="EmailMode.Smtp"/> – real outbound delivery. Required outside
/// Development/Testing; allowed everywhere.</item>
/// <item><see cref="EmailMode.Capture"/> – logs the rendered message instead of sending it.
/// ALLOWED only when the hosting environment is Development or Testing; any other
/// environment fails startup through <c>ValidateOnStart</c>.</item>
/// </list>
/// When <see cref="Mode"/> is unset, the framework default is Smtp everywhere except
/// Development/Testing, where it defaults to Capture so dev/test never needs SMTP.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Outbound email delivery mode. Optional: see <see cref="IsCaptureAllowed"/>
    /// for the implicit default per environment.
    /// </summary>
    public EmailMode Mode { get; set; } = EmailMode.Unset;

    /// <summary>
    /// True when <see cref="Mode"/> resolves to Capture for the given environment.
    /// </summary>
    public bool IsCaptureAllowed(IHostEnvironment environment)
    {
        var resolved = ResolveMode(environment);
        return resolved == EmailMode.Capture;
    }

    /// <summary>
    /// Compute the effective mode for the environment. Unset defaults to Capture in
    /// Development/Testing and to Smtp in every other environment.
    /// </summary>
    public EmailMode ResolveMode(IHostEnvironment environment)
    {
        if (Mode != EmailMode.Unset)
            return Mode;

        var name = environment.EnvironmentName;
        if (string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return EmailMode.Capture;
        }

        return EmailMode.Smtp;
    }

    /// <summary>
    /// Validate the resolved configuration against the hosting environment.
    /// Called by the options pipeline at startup (<c>ValidateOnStart</c>).
    /// </summary>
    public void Validate(IHostEnvironment environment)
    {
        var resolved = ResolveMode(environment);

        if (resolved == EmailMode.Capture
            && !string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Email:Mode=Capture is only permitted in Development or Testing. " +
                $"Current environment is '{environment.EnvironmentName}'. " +
                "Set Email:Mode=Smtp and configure Smtp:Host / Smtp:Port / Smtp:FromAddress.");
        }
    }
}

public enum EmailMode
{
    /// <summary>No explicit value; framework derives from environment.</summary>
    Unset = 0,

    /// <summary>Real outbound delivery via SMTP.</summary>
    Smtp = 1,

    /// <summary>Capture-only: log rendered messages; do not send.</summary>
    Capture = 2,
}
