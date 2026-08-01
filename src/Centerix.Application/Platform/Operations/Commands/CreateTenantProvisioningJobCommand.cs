namespace Centerix.Application.Platform.Operations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Operations;

using MediatR;

public record CreateTenantProvisioningJobCommand : IRequest<Result<Created>>;

public class CreateTenantProvisioningJobHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantProvisioningJobCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(CreateTenantProvisioningJobCommand request, CancellationToken cancellationToken)
    {
        var result = TenantProvisioningJob.Create(Guid.NewGuid());

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.TenantProvisioningJobs.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantProvisioningJob.Create",
            entityType: nameof(TenantProvisioningJob),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.Status,
                result.Value.RetryCount
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
