namespace Centerix.Domain.Auditing;

using Centerix.Domain.Common.Results;

public static class AuditLogErrors
{
    public static Error ActionRequired =>
        Error.Validation("AuditLog.Action_Required", "Audit action is required");
}
