using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Staff.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class PlatformPermissionsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.PlatformPermissions.Read)]
    public async Task<IActionResult> GetPlatformPermissions(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPlatformPermissionsQuery(), cancellationToken);

        return result.Match(
            permissions => Ok(permissions),
            Problem);
    }
}
