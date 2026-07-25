using Centerix.Application.Common.Interfaces;
using Centerix.Application.Students.Branches.Commands;
using Centerix.Application.Students.Branches.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class BranchesController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Branches.Read)]
    public async Task<IActionResult> GetBranches(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBranchesQuery(), cancellationToken);

        return result.Match(
            branches => Ok(branches),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Branches.Read)]
    public async Task<IActionResult> GetBranch(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBranchByIdQuery(id), cancellationToken);

        return result.Match(
            branch => Ok(branch),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Branches.Create)]
    public async Task<IActionResult> CreateBranch(CreateBranchCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.Branches.Update)]
    public async Task<IActionResult> UpdateBranch(Guid id, UpdateBranchCommand command, CancellationToken cancellationToken)
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
    [HasPermission(Permissions.Branches.Delete)]
    public async Task<IActionResult> DeleteBranch(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteBranchCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
