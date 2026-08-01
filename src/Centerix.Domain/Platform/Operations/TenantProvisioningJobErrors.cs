namespace Centerix.Domain.Platform.Operations;

using Centerix.Domain.Common.Results;

public static class TenantProvisioningJobErrors
{
    public static Error CannotStart =>
        Error.Conflict("TenantProvisioningJob.CannotStart", "Job can only be started from Pending or Failed status");

    public static Error AlreadyCompleted =>
        Error.Conflict("TenantProvisioningJob.AlreadyCompleted", "Job is already completed");

    public static Error ErrorMessageRequired =>
        Error.Validation("TenantProvisioningJob.ErrorMessage_Required", "Error message is required when marking a job as failed");

    public static Error NotFailed =>
        Error.Conflict("TenantProvisioningJob.NotFailed", "Job can only be retried from Failed status");
}
