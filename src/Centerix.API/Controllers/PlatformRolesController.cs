using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Staff.Commands;
using Centerix.Application.Platform.Staff.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class PlatformRolesController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.PlatformRoles.Read)]
    public async Task<IActionResult> GetPlatformRoles(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPlatformRolesQuery(), cancellationToken);

        return result.Match(
            roles => Ok(roles),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.PlatformRoles.Create)]
    public async Task<IActionResult> CreatePlatformRole(CreatePlatformRoleCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.PlatformRoles.Delete)]
    public async Task<IActionResult> DeletePlatformRole(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeletePlatformRoleCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
