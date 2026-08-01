namespace Centerix.Application.Platform.Operations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Operations;

using MediatR;

public record CompleteTenantProvisioningJobCommand(Guid Id) : IRequest<Result<Updated>>;

public class CompleteTenantProvisioningJobHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CompleteTenantProvisioningJobCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(CompleteTenantProvisioningJobCommand request, CancellationToken cancellationToken)
    {
        var job = await dbContext.TenantProvisioningJobs.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (job is null)
        {
            return Error.NotFound("TenantProvisioningJob.NotFound", $"Provisioning job with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            job.Status,
            job.CompletedAt
        });

        var completeResult = job.Complete();
        if (!completeResult.IsSuccess)
        {
            return completeResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantProvisioningJob.Complete",
            entityType: nameof(TenantProvisioningJob),
            entityId: job.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                job.Status,
                job.CompletedAt
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
