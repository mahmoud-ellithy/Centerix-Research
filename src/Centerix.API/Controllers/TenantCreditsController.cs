using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Billing.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantCreditsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TenantCredits.Read)]
    public async Task<IActionResult> GetTenantCredits(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantCreditsQuery(), cancellationToken);

        return result.Match(
            credits => Ok(credits),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantCredits.Create)]
    public async Task<IActionResult> CreateTenantCredit(CreateTenantCreditCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }
}
