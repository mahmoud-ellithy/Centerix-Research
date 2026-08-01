namespace Centerix.Domain.Platform.Operations;

using Centerix.Domain.Common.Results;

public static class TenantSchemaVersionErrors
{
    public static Error TenantIdRequired =>
        Error.Validation("TenantSchemaVersion.TenantId_Required", "Tenant ID is required");

    public static Error CurrentVersionRequired =>
        Error.Validation("TenantSchemaVersion.CurrentVersion_Required", "Current version is required");
}
