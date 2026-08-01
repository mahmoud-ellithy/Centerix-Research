using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Referrals.Commands;
using Centerix.Application.Platform.Referrals.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantReferralsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TenantReferrals.Read)]
    public async Task<IActionResult> GetTenantReferrals(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantReferralsQuery(), cancellationToken);

        return result.Match(
            referrals => Ok(referrals),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantReferrals.Create)]
    public async Task<IActionResult> CreateTenantReferral(CreateTenantReferralCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }
}
