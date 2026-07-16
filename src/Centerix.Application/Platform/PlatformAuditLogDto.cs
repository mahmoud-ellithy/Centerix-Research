namespace Centerix.Application.Platform;

public class PlatformAuditLogDto
{
    public long Id { get; set; }
    public string TenantId { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? IPAddress { get; set; }
    public DateTime CreatedAt { get; set; }
}
