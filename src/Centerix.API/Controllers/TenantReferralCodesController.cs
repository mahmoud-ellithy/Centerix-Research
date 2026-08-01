using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Referrals.Commands;
using Centerix.Application.Platform.Referrals.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantReferralCodesController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TenantReferralCodes.Read)]
    public async Task<IActionResult> GetTenantReferralCodes(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantReferralCodesQuery(), cancellationToken);

        return result.Match(
            referralCodes => Ok(referralCodes),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantReferralCodes.Create)]
    public async Task<IActionResult> CreateTenantReferralCode(CreateTenantReferralCodeCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }
}
