using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Tenants;
using Centerix.Application.Platform.Tenants.Commands;
using Centerix.Application.Platform.Tenants.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Tenants.Read)]
    public async Task<IActionResult> GetTenants(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantsQuery(), cancellationToken);

        return result.Match(
            tenants => Ok(tenants),
            Problem);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Tenants.Read)]
    public async Task<IActionResult> GetTenant(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantByIdQuery(id), cancellationToken);

        return result.Match(
            tenant => Ok(tenant),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Tenants.Create)]
    public async Task<IActionResult> CreateTenant(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> UpdateTenant(Guid id, UpdateTenantCommand command, CancellationToken cancellationToken)
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

    [HttpPost("{id:guid}/suspend")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> SuspendTenant(Guid id, SuspendTenantCommand command, CancellationToken cancellationToken)
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

    [HttpPost("{id:guid}/reactivate")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> ReactivateTenant(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ReactivateTenantCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Tenants.Delete)]
    public async Task<IActionResult> CancelTenant(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelTenantCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
