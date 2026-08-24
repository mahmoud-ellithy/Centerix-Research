using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Invitations.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/memberships")]
public class MembershipsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet("me")]
    [HasPermission(Permissions.Memberships.Read)]
    public async Task<IActionResult> GetMyMemberships(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMyMembershipsQuery(), cancellationToken);

        return result.Match(
            memberships => Ok(memberships),
            Problem);
    }
}
