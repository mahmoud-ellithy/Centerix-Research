namespace Centerix.Domain.Platform.Operations;

using Centerix.Domain.Common;

public class TenantSchemaVersion : Entity
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = default!;
    public string CurrentVersion { get; private set; } = default!;
    public DateTime LastMigratedAt { get; private set; }

    private TenantSchemaVersion() { }

    private TenantSchemaVersion(
        Guid id,
        string tenantId,
        string currentVersion,
        DateTime lastMigratedAt)
    {
        Id = id;
        TenantId = tenantId;
        CurrentVersion = currentVersion;
        LastMigratedAt = lastMigratedAt;
    }

    public static TenantSchemaVersion Create(
        Guid id,
        string tenantId,
        string currentVersion,
        DateTime lastMigratedAt)
    {
        return new TenantSchemaVersion(id, tenantId, currentVersion, lastMigratedAt);
    }

    public void MigrateTo(string newVersion)
    {
        CurrentVersion = newVersion;
        LastMigratedAt = DateTime.UtcNow;
    }
}
