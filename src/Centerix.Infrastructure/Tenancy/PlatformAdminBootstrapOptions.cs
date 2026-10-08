using System.ComponentModel.DataAnnotations;

namespace Centerix.Infrastructure.Tenancy;

/// <summary>
/// First-PlatformAdmin production bootstrap contract (Batch 2 correction).
/// Section: "PlatformAdminBootstrap".
/// Purpose: create the FIRST PlatformAdmin account in production. This is an operational
/// bootstrap mechanism — NOT development sample-data seeding — and is fully independent of
/// <c>DatabaseInitialization:SeedDevelopmentData</c> (which stays false in production).
/// Production never invents a password: the temporary password MUST be supplied explicitly
/// through secure configuration (environment variable / secret store). Startup fails clearly
/// when the enabled configuration is incomplete, and the bootstrap is idempotent: an
/// existing PlatformAdmin is never recreated, reset, or modified.
/// </summary>
public sealed class PlatformAdminBootstrapOptions
{
    public const string SectionName = "PlatformAdminBootstrap";

    /// <summary>
    /// Master switch. Default false: production never bootstraps an admin unless an
    /// operator explicitly enables this section.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Email (user name) of the first PlatformAdmin. Required when enabled.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Explicit temporary password for the first PlatformAdmin. Required when enabled.
    /// Never ships a default; must satisfy the Identity password rules (validated at creation).
    /// </summary>
    public string TemporaryPassword { get; set; } = string.Empty;

    /// <summary>
    /// Validates the ENABLED configuration. A disabled section is always valid (bootstrap skipped).
    /// An enabled-but-incomplete section throws so startup fails clearly instead of silently
    /// skipping the first-admin creation.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Email) || !new EmailAddressAttribute().IsValid(Email.Trim()))
            throw new InvalidOperationException(
                "PlatformAdminBootstrap:Email must be configured with a valid email address when " +
                "PlatformAdminBootstrap:Enabled is true. Refusing to bootstrap the first " +
                "PlatformAdmin without an explicitly configured operator email.");

        if (string.IsNullOrWhiteSpace(TemporaryPassword))
            throw new InvalidOperationException(
                "PlatformAdminBootstrap:TemporaryPassword must be configured (environment variable, " +
                "secret store, or development user-secrets) when PlatformAdminBootstrap:Enabled is true. " +
                "Refusing to bootstrap the first PlatformAdmin without an explicitly configured " +
                "temporary password. There is no generated or default password.");
    }
}
