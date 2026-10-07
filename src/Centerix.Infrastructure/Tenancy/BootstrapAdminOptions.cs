namespace Centerix.Infrastructure.Tenancy;

/// <summary>
/// Optional bootstrap-administrator configuration (NEW-1 / NEW-2).
/// Section: "BootstrapAdmin".
/// Production never invents a static/default password: when development seed
/// data is enabled in production, the temporary password MUST come from this
/// configuration (environment variable / secret store). Startup fails clearly
/// when it is missing or does not satisfy Identity password rules.
/// In non-production environments the seeder generates a cryptographically
/// random temporary password when no explicit value is configured.
/// </summary>
public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";

    /// <summary>Explicit temporary password for the bootstrap admin. Never ships a default.</summary>
    public string TemporaryPassword { get; set; } = string.Empty;
}
