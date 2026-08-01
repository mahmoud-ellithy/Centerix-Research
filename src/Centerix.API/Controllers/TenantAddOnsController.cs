using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Subscriptions.Commands;
using Centerix.Application.Platform.Subscriptions.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantAddOnsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TenantAddOns.Read)]
    public async Task<IActionResult> GetTenantAddOns(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantAddOnsQuery(), cancellationToken);

        return result.Match(
            tenantAddOns => Ok(tenantAddOns),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantAddOns.Create)]
    public async Task<IActionResult> CreateTenantAddOn(CreateTenantAddOnCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPost("{id}/cancel")]
    [HasPermission(Permissions.TenantAddOns.Update)]
    public async Task<IActionResult> CancelTenantAddOn(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelTenantAddOnCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
