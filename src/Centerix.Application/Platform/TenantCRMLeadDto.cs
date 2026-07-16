namespace Centerix.Application.Platform;

public class TenantCRMLeadDto
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public string CenterName { get; set; } = default!;
    public string ContactName { get; set; } = default!;
    public string Phone { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string Stage { get; set; } = default!;
    public string? StageLabel { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime CreatedAt { get; set; }
}
