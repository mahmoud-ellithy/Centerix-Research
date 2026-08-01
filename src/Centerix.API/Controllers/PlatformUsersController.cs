using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Staff.Commands;
using Centerix.Application.Platform.Staff.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class PlatformUsersController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.PlatformUsers.Read)]
    public async Task<IActionResult> GetPlatformUsers(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPlatformUsersQuery(), cancellationToken);

        return result.Match(
            users => Ok(users),
            Problem);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.PlatformUsers.Read)]
    public async Task<IActionResult> GetPlatformUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPlatformUserByIdQuery(id), cancellationToken);

        return result.Match(
            user => Ok(user),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.PlatformUsers.Create)]
    public async Task<IActionResult> CreatePlatformUser(CreatePlatformUserCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.PlatformUsers.Update)]
    public async Task<IActionResult> UpdatePlatformUser(Guid id, UpdatePlatformUserCommand command, CancellationToken cancellationToken)
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

    [HttpPost("{id:guid}/deactivate")]
    [HasPermission(Permissions.PlatformUsers.Update)]
    public async Task<IActionResult> DeactivatePlatformUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdatePlatformUserCommand(id, null, false), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id:guid}/reactivate")]
    [HasPermission(Permissions.PlatformUsers.Update)]
    public async Task<IActionResult> ReactivatePlatformUser(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdatePlatformUserCommand(id, null, true), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
