namespace Centerix.Application.Platform.Operations.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using MediatR;

using Mapster;

public record GetTenantProvisioningJobByIdQuery(Guid Id) : IRequest<Result<TenantProvisioningJobDto>>;

public class GetTenantProvisioningJobByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantProvisioningJobByIdQuery, Result<TenantProvisioningJobDto>>
{
    public async Task<Result<TenantProvisioningJobDto>> Handle(GetTenantProvisioningJobByIdQuery request, CancellationToken cancellationToken)
    {
        var job = await dbContext.TenantProvisioningJobs
            .Where(j => j.Id == request.Id)
            .ProjectToType<TenantProvisioningJobDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return Error.NotFound("TenantProvisioningJob.NotFound", $"Provisioning job with id '{request.Id}' was not found.");
        }

        return job;
    }
}
