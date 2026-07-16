using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class PlansController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Plans.Read)]
    public async Task<IActionResult> GetPlans(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPlansQuery(), cancellationToken);

        return result.Match(
            plans => Ok(plans),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Plans.Read)]
    public async Task<IActionResult> GetPlan(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPlanByIdQuery(id), cancellationToken);

        return result.Match(
            plan => Ok(plan),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Plans.Create)]
    public async Task<IActionResult> CreatePlan(CreatePlanCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.Plans.Update)]
    public async Task<IActionResult> UpdatePlan(int id, UpdatePlanCommand command, CancellationToken cancellationToken)
    {
        if (id != command.Id)
        {
            return BadRequest(new { detail = "Route id does not match command id." });
        }

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpDelete("{id}")]
    [HasPermission(Permissions.Plans.Delete)]
    public async Task<IActionResult> DeletePlan(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeletePlanCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}