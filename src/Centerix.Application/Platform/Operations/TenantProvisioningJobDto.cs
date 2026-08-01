namespace Centerix.Application.Platform.Operations;

public class TenantProvisioningJobDto
{
    public Guid Id { get; set; }
    public byte Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public byte RetryCount { get; set; }
}
