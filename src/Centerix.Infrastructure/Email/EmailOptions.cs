namespace Centerix.Infrastructure.Email;

using Microsoft.Extensions.Hosting;

/// <summary>
/// Email delivery mode (REV-012). Section: "Email".
/// <list type="bullet">
/// <item><see cref="EmailMode.Smtp"/> – real outbound delivery. Required outside
/// Development (and the test host); allowed everywhere.</item>
/// <item><see cref="EmailMode.Capture"/> – logs the rendered message instead of sending it.
/// ALLOWED only when the hosting environment is Development, or is "Testing" AND the caller
/// passes <c>allowTestingCapture: true</c> (REV-012 A.1: the environment NAME "Testing" is
/// never trusted by itself — see <see cref="Centerix.Infrastructure.Common.TestHostMarkers"/>).
/// Any other environment, or "Testing" without the marker, fails startup through
/// <c>ValidateOnStart</c>.</item>
/// </list>
/// When <see cref="Mode"/> is unset, the framework default is Smtp everywhere except
/// Development (and "Testing" with the marker), where it defaults to Capture so dev/test
/// never needs SMTP.
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
    public bool IsCaptureAllowed(IHostEnvironment environment, bool allowTestingCapture = false)
    {
        var resolved = ResolveMode(environment, allowTestingCapture);
        return resolved == EmailMode.Capture;
    }

    /// <summary>
    /// Compute the effective mode for the environment. Unset defaults to Capture in
    /// Development (or "Testing" when <paramref name="allowTestingCapture"/> is true) and to
    /// Smtp everywhere else.
    /// </summary>
    public EmailMode ResolveMode(IHostEnvironment environment, bool allowTestingCapture = false)
    {
        if (Mode != EmailMode.Unset)
            return Mode;

        if (IsDevelopmentOrAllowedTesting(environment, allowTestingCapture))
            return EmailMode.Capture;

        return EmailMode.Smtp;
    }

    /// <summary>
    /// Validate the resolved configuration against the hosting environment. Called explicitly
    /// from <c>Program.cs</c> once <see cref="IHostEnvironment"/> is available at startup — NOT
    /// via <c>ValidateOnStart</c> (the options pipeline cannot inject <see cref="IHostEnvironment"/>
    /// into a validator without building a second <see cref="IServiceProvider"/>).
    /// </summary>
    public void Validate(IHostEnvironment environment, bool allowTestingCapture = false)
    {
        var resolved = ResolveMode(environment, allowTestingCapture);

        if (resolved == EmailMode.Capture && !IsDevelopmentOrAllowedTesting(environment, allowTestingCapture))
        {
            throw new InvalidOperationException(
                $"Email:Mode=Capture is only permitted in Development, or in an environment named " +
                $"Testing when the test-host marker '{Centerix.Infrastructure.Common.TestHostMarkers.AllowCaptureAndSkipDbInitKey}' " +
                $"is set. Current environment is '{environment.EnvironmentName}'" +
                (string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase)
                    ? " (marker not set)."
                    : ".") +
                " Set Email:Mode=Smtp and configure Smtp:Host / Smtp:Port / Smtp:FromAddress.");
        }
    }

    private static bool IsDevelopmentOrAllowedTesting(IHostEnvironment environment, bool allowTestingCapture)
    {
        if (string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
            return true;

        return allowTestingCapture
            && string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);
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
