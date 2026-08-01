using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Operations.Commands;
using Centerix.Application.Platform.Operations.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantProvisioningJobsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TenantProvisioningJobs.Read)]
    public async Task<IActionResult> GetTenantProvisioningJobs(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantProvisioningJobsQuery(), cancellationToken);

        return result.Match(
            jobs => Ok(jobs),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.TenantProvisioningJobs.Read)]
    public async Task<IActionResult> GetTenantProvisioningJob(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantProvisioningJobByIdQuery(id), cancellationToken);

        return result.Match(
            job => Ok(job),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantProvisioningJobs.Create)]
    public async Task<IActionResult> CreateTenantProvisioningJob(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateTenantProvisioningJobCommand(), cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPost("{id}/complete")]
    [HasPermission(Permissions.TenantProvisioningJobs.Update)]
    public async Task<IActionResult> CompleteTenantProvisioningJob(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CompleteTenantProvisioningJobCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
