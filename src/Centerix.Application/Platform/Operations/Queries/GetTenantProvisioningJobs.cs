namespace Centerix.Application.Platform.Operations.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetTenantProvisioningJobsQuery : IRequest<Result<IEnumerable<TenantProvisioningJobDto>>>;

public class GetTenantProvisioningJobsHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantProvisioningJobsQuery, Result<IEnumerable<TenantProvisioningJobDto>>>
{
    public async Task<Result<IEnumerable<TenantProvisioningJobDto>>> Handle(
        GetTenantProvisioningJobsQuery request,
        CancellationToken cancellationToken)
    {
        var jobs = await dbContext.TenantProvisioningJobs
            .ProjectToType<TenantProvisioningJobDto>()
            .ToListAsync(cancellationToken);

        return jobs;
    }
}
