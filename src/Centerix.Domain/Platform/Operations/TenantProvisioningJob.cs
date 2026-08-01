namespace Centerix.Domain.Platform.Operations;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Operations.Enums;

public class TenantProvisioningJob : AuditableEntity<Guid>
{
    public ProvisioningJobStatus Status { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }
    public byte RetryCount { get; private set; }

    private TenantProvisioningJob() { }

    private TenantProvisioningJob(
        Guid id,
        ProvisioningJobStatus status,
        DateTime? startedAt,
        DateTime? completedAt,
        string? errorMessage,
        byte retryCount)
        : base(id)
    {
        Status = status;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ErrorMessage = errorMessage;
        RetryCount = retryCount;
    }

    public static Result<TenantProvisioningJob> Create(Guid id)
    {
        return new TenantProvisioningJob(
            id,
            ProvisioningJobStatus.Pending,
            null,
            null,
            null,
            0);
    }

    public Result<Updated> Start()
    {
        if (Status != ProvisioningJobStatus.Pending && Status != ProvisioningJobStatus.Failed)
            return TenantProvisioningJobErrors.CannotStart;

        Status = ProvisioningJobStatus.Creating;
        StartedAt = DateTime.UtcNow;
        ErrorMessage = null;

        return Result.Updated;
    }

    public Result<Updated> Complete()
    {
        if (Status == ProvisioningJobStatus.Ready)
            return TenantProvisioningJobErrors.AlreadyCompleted;

        Status = ProvisioningJobStatus.Ready;
        CompletedAt = DateTime.UtcNow;

        return Result.Updated;
    }

    public Result<Updated> Fail(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return TenantProvisioningJobErrors.ErrorMessageRequired;

        Status = ProvisioningJobStatus.Failed;
        ErrorMessage = errorMessage;

        return Result.Updated;
    }

    public Result<Updated> Retry()
    {
        if (Status != ProvisioningJobStatus.Failed)
            return TenantProvisioningJobErrors.NotFailed;

        RetryCount++;
        Status = ProvisioningJobStatus.Pending;
        ErrorMessage = null;

        return Result.Updated;
    }
}
